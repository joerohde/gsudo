﻿using gsudo.Helpers;
using gsudo.Native;
using gsudo.Rpc;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace gsudo.ProcessRenderers
{
    /// <summary>
    /// This renderer starts the process in suspended status, and the elevated TokenSwitchHost will apply a different token to us.
    /// </summary>
    class TokenSwitchRenderer : IProcessRenderer
    {
        private readonly Connection _connection;
        private readonly ElevationRequest _elevationRequest;
        private readonly SafeProcessHandle _process;
        private readonly ProcessApi.PROCESS_INFORMATION _processInformation;
        private readonly ManualResetEventSlim tokenSwithSuccessEvent = new ManualResetEventSlim(false);

        public TokenSwitchRenderer(Connection connection, ElevationRequest elevationRequest)
        {
            if (Settings.SecurityEnforceUacIsolation && !elevationRequest.NewWindow)
                throw new Exception("TokenSwitch mode not supported when SecurityEnforceUacIsolation is set.");

            _connection = connection;
            _elevationRequest = elevationRequest;

            ProcessApi.CreateProcessFlags dwCreationFlags = ProcessApi.CreateProcessFlags.CREATE_SUSPENDED;

            if (elevationRequest.NewWindow)
                dwCreationFlags |= ProcessApi.CreateProcessFlags.CREATE_NEW_CONSOLE;

            Environment.SetEnvironmentVariable("prompt", Environment.ExpandEnvironmentVariables(elevationRequest.Prompt));

            // Now, we have an issue with this methodÑ The process launched with the new token throws Access Denied if it tries to read its own token.
            // Kind of dirty workaround is to wrap the call with a "CMD.exe /c ".. this intermediate process will then
            // launching the command with a fresh new (desired) token and we know cmd wont try to read it's substitute token (throwing Access Denied).  

            string exeName, args;
            if (ArgumentsHelper.UnQuote(elevationRequest.FileName.ToUpperInvariant()) != Environment.GetEnvironmentVariable("COMSPEC").ToUpperInvariant())
            {
                exeName = Environment.GetEnvironmentVariable("COMSPEC");
                args = $"/s /c \"{elevationRequest.FileName} {elevationRequest.Arguments}\"";
            }
            else
            {
                // Hack not needed if we are already calling CMD
                exeName = elevationRequest.FileName;
                args = elevationRequest.Arguments;
            }

            _process = ProcessFactory.CreateProcessWithFlags(exeName, args, dwCreationFlags, out _processInformation);

            elevationRequest.TargetProcessId = _processInformation.dwProcessId;
            if (!elevationRequest.NewWindow)
                ConsoleApi.SetConsoleCtrlHandler(ConsoleHelper.IgnoreConsoleCancelKeyPress, true);
        }

        public Task<int> Start()
        {
            try
            {
                var t1 = new StreamReader(_connection.ControlStream).ConsumeOutput(HandleControlStream);

                WaitHandle.WaitAny(new WaitHandle[] { tokenSwithSuccessEvent.WaitHandle, _process.GetProcessWaitHandle(), _connection.DisconnectedWaitHandle });

                if (!tokenSwithSuccessEvent.IsSet)
                {
                    ProcessApi.TerminateProcess(_process.DangerousGetHandle(), 0);
                    return Task.FromResult(Constants.GSUDO_ERROR_EXITCODE);
                }

                _ = ProcessApi.ResumeThread(_processInformation.hThread);

                if (_elevationRequest.Wait)
                {
                    _process.GetProcessWaitHandle().WaitOne();
                    if (ProcessApi.GetExitCodeProcess(_process, out int exitCode))
                        return Task.FromResult(exitCode);
                }

                return Task.FromResult(0);
            }
            finally
            {
                ConsoleApi.SetConsoleCtrlHandler(ConsoleHelper.IgnoreConsoleCancelKeyPress, false);
            }
        }

        enum Mode { Normal, Error};
        Mode CurrentMode = Mode.Normal;

        static readonly string[] TOKENS = new string[] { "\0", Constants.TOKEN_ERROR, Constants.TOKEN_SUCCESS };
        
        private async Task HandleControlStream(string s)
        {
            Action<Mode> Toggle = (m) => CurrentMode = CurrentMode == Mode.Normal ? m : Mode.Normal;

            var tokens = new Stack<string>(StringTokenizer.Split(s, TOKENS).Reverse());

            while (tokens.Count > 0)
            {
                var token = tokens.Pop();

                if (token == "\0") continue; // session keep alive
                if (token == Constants.TOKEN_SUCCESS)
                {
                    tokenSwithSuccessEvent.Set();
                    Logger.Instance.Log("Process token successfully substituted.", LogLevel.Debug);
                    continue;
                }
                if (token == Constants.TOKEN_ERROR)
                {
                    //fix intercalation of messages;
                    await Console.Error.FlushAsync().ConfigureAwait(false);
                    await Console.Out.FlushAsync().ConfigureAwait(false);

                    Toggle(Mode.Error);
                    Console.ResetColor();
                    continue;
                }
                if (CurrentMode == Mode.Error)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write(token);
                    Console.ResetColor();
                    continue;
                }

                Console.Write(token);
            }
        }

    }
}
