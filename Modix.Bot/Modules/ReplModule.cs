﻿using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using Modix.Data.Models.Core;
using Modix.Services.AutoCodePaste;
using Modix.Services.AutoRemoveMessage;
using Modix.Services.Utilities;
using Newtonsoft.Json;

namespace Modix.Modules
{
    public class Result
    {
        public object ReturnValue { get; set; }
        public string Exception { get; set; }
        public string Code { get; set; }
        public string ExceptionType { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public TimeSpan CompileTime { get; set; }
        public string ConsoleOut { get; set; }
        public string ReturnTypeName { get; set; }
    }

    [Name("Repl"), Summary("Execute & demonstrate code snippets.")]
    public class ReplModule : ModuleBase
    {
        // 1024 is the maximum field embed size
        // minus 3 for the start codeblocks
        // minus 3 for the end codeblocks
        // minus 1 for the first newline after the ```cs
        // minus 2 for 'cs'
        // minus 1 for the next newline after the code
        // THIS LIMIT IS NOT ARBITRARY
        private const int MaxReplSize = 1_024 - 3 - 3 - 1 - 2 - 1;
        
        private const string DefaultReplRemoteUrl = "http://csdiscord-repl-service:31337/Eval";
        private readonly string _replUrl;
        private readonly CodePasteService _pasteService;
        private readonly IAutoRemoveMessageService _autoRemoveMessageService;
        private readonly IHttpClientFactory _httpClientFactory;

        public ReplModule(
            CodePasteService pasteService,
            IAutoRemoveMessageService autoRemoveMessageService,
            IHttpClientFactory httpClientFactory,
            IOptions<ModixConfig> modixConfig)
        {
            _pasteService = pasteService;
            _autoRemoveMessageService = autoRemoveMessageService;
            _httpClientFactory = httpClientFactory;
            _pasteService = pasteService;
            _replUrl = string.IsNullOrWhiteSpace(modixConfig.Value.ReplUrl) ? DefaultReplRemoteUrl : modixConfig.Value.ReplUrl;
        }

        [Command("exec"), Alias("eval"), Summary("Executes the given C# code and returns the result.")]
        public async Task ReplInvokeAsync(
            [Remainder]
            [Summary("The code to execute.")]
                string code)
        {
            if (!(Context.Channel is SocketGuildChannel) || !(Context.User is SocketGuildUser guildUser))
            {
                await ModifyOrSendErrorEmbed("The REPL can only be executed in public guild channels.");
                return;
            }

            if (code.Length > MaxReplSize)
            {
                await ModifyOrSendErrorEmbed($"Code to execute cannot be longer than {MaxReplSize} characters.");
                return;
            }

            var message = await Context.Channel
                .SendMessageAsync(embed: new EmbedBuilder()
                    .WithTitle("REPL Executing")
                    .WithAuthor(Context.User)
                    .WithColor(Color.LightOrange)
                    .WithDescription($"Compiling and Executing [your code]({Context.Message.GetJumpUrl()})...")
                    .Build());

            var content = FormatUtilities.BuildContent(code);

            HttpResponseMessage res;
            try
            {
                var client = _httpClientFactory.CreateClient();

                using (var tokenSrc = new CancellationTokenSource(30000))
                {
                    res = await _httpClientFactory.CreateClient().PostAsync(_replUrl, content, tokenSrc.Token);
                }
            }
            catch (TaskCanceledException)
            {
                await ModifyOrSendErrorEmbed("Gave up waiting for a response from the REPL service.", message);
                return;
            }
            catch (IOException ex)
            {
                await ModifyOrSendErrorEmbed("Recieved an invalid response from the REPL service." +
                                             $"\n\n{Format.Bold("Details:")}\n{ex.Message}", message);
                return;
            }
            catch (Exception ex)
            {
                await ModifyOrSendErrorEmbed("An error occurred while sending a request to the REPL service. " +
                                             "This is probably due to container exhaustion - try again later." +
                                             $"\n\n{Format.Bold("Details:")}\n{ex.Message}", message);
                return;
            }

            if (!res.IsSuccessStatusCode & res.StatusCode != HttpStatusCode.BadRequest)
            {
                await ModifyOrSendErrorEmbed($"Status Code: {(int)res.StatusCode} {res.StatusCode}", message);
                return;
            }

            var parsedResult = JsonConvert.DeserializeObject<Result>(await res.Content.ReadAsStringAsync());

            var embed = await BuildEmbedAsync(guildUser, parsedResult);

            await _autoRemoveMessageService.RegisterRemovableMessageAsync(message, Context.User, embed);

            await message.ModifyAsync(a =>
            {
                a.Content = string.Empty;
                a.Embed = embed.Build();
            });

            await Context.Message.DeleteAsync();
        }

        private async Task ModifyOrSendErrorEmbed(string error, IUserMessage message = null)
        {
            var embed = new EmbedBuilder()
                .WithTitle("REPL Error")
                .WithAuthor(Context.User)
                .WithColor(Color.Red)
                .AddField("Tried to execute", $"[this code]({Context.Message.GetJumpUrl()})")
                .WithDescription(error);

            if (message == null)
            {
                await ReplyAsync(embed: embed.Build());
            }
            else
            {
                await message.ModifyAsync(msg =>
                {
                    msg.Content = null;
                    msg.Embed = embed.Build();
                });
            }
        }

        private async Task<EmbedBuilder> BuildEmbedAsync(SocketGuildUser guildUser, Result parsedResult)
        {
            var returnValue = parsedResult.ReturnValue?.ToString() ?? " ";
            var consoleOut = parsedResult.ConsoleOut;

            var embed = new EmbedBuilder()
                .WithTitle("REPL Result")
                .WithDescription(string.IsNullOrEmpty(parsedResult.Exception) ? "Success" : "Failure")
                .WithColor(string.IsNullOrEmpty(parsedResult.Exception) ? Color.Green : Color.Red)
                .WithAuthor(guildUser)
                .WithFooter(a => a.WithText($"Compile: {parsedResult.CompileTime.TotalMilliseconds:F}ms | Execution: {parsedResult.ExecutionTime.TotalMilliseconds:F}ms"));

            embed.AddField(a => a.WithName("Code").WithValue(Format.Code(parsedResult.Code, "cs")));

            // in the following code, 'size' is - 2 (minus 'cs') and then added with however long the embed type is
            // TODO: this is a lot of repeated code
            if (parsedResult.ReturnValue != null)
            {
                const string embedType = "json";
                var size = MaxReplSize + 2 - embedType.Length;
                embed.AddField(a => a.WithName($"Result: {parsedResult.ReturnTypeName ?? "null"}")
                                     .WithValue(Format.Code($"{returnValue.TruncateTo(size)}", embedType)));
                await embed.UploadToServiceIfBiggerThan(returnValue, embedType, size, _pasteService);
            }

            if (!string.IsNullOrWhiteSpace(consoleOut))
            {
                const string embedType = "txt";
                var size = MaxReplSize + 2 - embedType.Length;
                embed.AddField(a => a.WithName("Console Output")
                                     .WithValue(Format.Code(consoleOut.TruncateTo(size), embedType)));
                await embed.UploadToServiceIfBiggerThan(consoleOut, embedType, size, _pasteService);
            }

            if (!string.IsNullOrWhiteSpace(parsedResult.Exception))
            {
                const string embedType = "diff";
                var size = MaxReplSize + 2 - embedType.Length;
                var diffFormatted = Regex.Replace(parsedResult.Exception, "^", "- ", RegexOptions.Multiline);
                embed.AddField(a => a.WithName($"Exception: {parsedResult.ExceptionType}")
                                     .WithValue(Format.Code(diffFormatted.TruncateTo(size), embedType)));
                await embed.UploadToServiceIfBiggerThan(diffFormatted, embedType, size, _pasteService);
            }

            return embed;
        }
    }
}
