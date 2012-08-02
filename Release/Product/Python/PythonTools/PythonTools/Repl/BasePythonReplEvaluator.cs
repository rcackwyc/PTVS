﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.PythonTools.Analysis;
using Microsoft.PythonTools.Debugger;
using Microsoft.PythonTools.Debugger.DebugEngine;
using Microsoft.PythonTools.Intellisense;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.Language;
using Microsoft.PythonTools.Options;
using Microsoft.PythonTools.Parsing;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Repl;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor.OptionsExtensionMethods;
using Microsoft.VisualStudio.Text.Projection;

namespace Microsoft.PythonTools.Repl {
#if INTERACTIVE_WINDOW
    using IReplWindow = IInteractiveWindow;
    using IReplEvaluator = IInteractiveEngine;
#endif

    internal abstract class BasePythonReplEvaluator : IReplEvaluator, IMultipleScopeEvaluator, IPythonReplIntellisense {
        private ListenerThread _curListener;
        private IReplWindow _window;
        private bool _multipleScopes = true, _attached;
        private PythonInteractiveCommonOptions _options;
        internal Task<ExecutionResult> _lastExecutionResult;

        internal static readonly object InputBeforeReset = new object();    // used to mark buffers which are no longer valid because we've done a reset

        private static readonly byte[] RunCommandBytes = MakeCommand("run ");
        private static readonly byte[] AbortCommandBytes = MakeCommand("abrt");
        private static readonly byte[] SetThreadAndFrameCommandBytes = MakeCommand("sett");
        private static readonly byte[] ExitCommandBytes = MakeCommand("exit");
        private static readonly byte[] GetSignaturesCommandBytes = MakeCommand("sigs");
        private static readonly byte[] GetMembersCommandBytes = MakeCommand("mems");
        private static readonly byte[] GetModulesListCommandBytes = MakeCommand("mods");
        private static readonly byte[] SetModuleCommandBytes = MakeCommand("setm");
        private static readonly byte[] InputLineCommandBytes = MakeCommand("inpl");
        private static readonly byte[] ExecuteFileCommandBytes = MakeCommand("excf");
        private static readonly byte[] DebugAttachCommandBytes = MakeCommand("dbga");

        protected BasePythonReplEvaluator() {
        }

        protected abstract PythonLanguageVersion AnalyzerProjectLanguageVersion { get; }

        protected abstract PythonLanguageVersion LanguageVersion { get; }

        internal abstract string DisplayName { get; }

        internal PythonInteractiveCommonOptions CurrentOptions {
            get {
                if (PythonToolsPackage.Instance == null) {
                    // running outside of VS, make this work for tests.
                    if (_options == null) {
                        _options = CreatePackageOptions();
                    }
                    return _options;
                }
                return GetPackageOptions();
            }
            set {
                _options = value;
            }
        }

        protected abstract PythonInteractiveCommonOptions CreatePackageOptions();
        protected abstract PythonInteractiveCommonOptions GetPackageOptions();

        protected void SetMultipleScopes(bool multipleScopes) {
            if (multipleScopes != _multipleScopes) {
                var multiScopeSupportChanged = MultipleScopeSupportChanged;
                if (multiScopeSupportChanged != null) {
                    multiScopeSupportChanged(this, EventArgs.Empty);
                }
                _multipleScopes = multipleScopes;
            }
        }

        protected void EnsureConnected() {
            if (_curListener == null) {
                Connect();
            }
        }

        #region IReplEvaluator Members

        public Task<ExecutionResult> Initialize(IReplWindow window) {
            _window = window;
            _window.SetOptionValue(ReplOptions.CommandPrefix, "$");

            window.SetOptionValue(ReplOptions.UseSmartUpDown, CurrentOptions.ReplSmartHistory);
            UpdatePrompts(true);
            window.SetOptionValue(ReplOptions.DisplayPromptInMargin, !CurrentOptions.InlinePrompts);
            window.SetOptionValue(ReplOptions.SupportAnsiColors, true);
            window.SetOptionValue(ReplOptions.FormattedPrompts, true);

            window.WriteLine("Python interactive window.  Type $help for a list of commands.");

            _window.TextView.BufferGraph.GraphBuffersChanged += BufferGraphGraphBuffersChanged;
            return ExecutionResult.Succeeded;
        }

        public void ActiveLanguageBufferChanged(ITextBuffer currentBuffer, ITextBuffer previousBuffer) {
        }

        private void BufferGraphGraphBuffersChanged(object sender, GraphBuffersChangedEventArgs e) {
            foreach (var removed in e.RemovedBuffers) {
                BufferParser parser;
                if (removed.Properties.TryGetProperty(typeof(BufferParser), out parser)) {
                    parser.RemoveBuffer(removed);
                }
            }
        }

        protected abstract void Connect();

        protected static void CreateConnection(out Socket conn, out int portNum) {
            conn = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.IP);
            conn.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            conn.Listen(0);
            portNum = ((IPEndPoint)conn.LocalEndPoint).Port;
        }

        protected void CreateListener(Socket conn, bool redirectStdOutput, Process process) {
            _curListener = new ListenerThread(this, conn, redirectStdOutput, process);
        }

        class ListenerThread {
            private readonly BasePythonReplEvaluator _eval;
            private readonly object _socketLock = new object();
            private readonly Process _process;
            internal bool _connected;
            private Socket _socket;
            private TaskCompletionSource<ExecutionResult> _completion;
            private string _executionText, _executionFile, _executionExtraArgs;
            private AutoResetEvent _completionResultEvent = new AutoResetEvent(false);
            private OverloadDoc[] _overloads;
            private Dictionary<string, string> _fileToModuleName;
            private Dictionary<string, bool> _allModules;
            private StringBuilder _preConnectionOutput;
            internal string _currentScope = "__main__";
            private MemberResults _memberResults;
            internal string _prompt1 = ">>> ", _prompt2 = "... ";
#if DEBUG
            private Thread _socketLockedThread;
#endif

            public ListenerThread(BasePythonReplEvaluator evaluator, Socket socket, bool redirectOutput, Process process) {
                _eval = evaluator;
                _socket = socket;
                _process = process;

                var outputThread = new Thread(OutputThread);
                outputThread.Name = "PythonReplEvaluator: " + evaluator.DisplayName;
                outputThread.Start();

                if (redirectOutput) {
                    _process.OutputDataReceived += new DataReceivedEventHandler(StdOutReceived);
                    _process.ErrorDataReceived += new DataReceivedEventHandler(StdErrReceived);
                    _process.EnableRaisingEvents = true;
                    _process.Exited += new EventHandler(ProcessExited);

                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();
                }
            }

            private void ProcessExited(object sender, EventArgs e) {
                Window.WriteError("The Python REPL process has exited\r\n");
                if (_preConnectionOutput != null) {
                    lock (_preConnectionOutput) {
                        Window.WriteError(FixNewLines(_preConnectionOutput.ToString()));
                    }
                }
            }

            private void StdErrReceived(object sender, DataReceivedEventArgs e) {
                if (e.Data != null) {
                    if (!_connected) {
                        AppendPreConnectionOutput(e);
                    } else {
                        Window.WriteError(e.Data + Environment.NewLine);
                    }
                }
            }

            private void StdOutReceived(object sender, DataReceivedEventArgs e) {
                if (e.Data != null) {
                    if (!_connected) {
                        AppendPreConnectionOutput(e);
                    } else {
                        Window.WriteOutput(FixNewLines(e.Data) + Environment.NewLine);
                    }
                }
            }

            private void AppendPreConnectionOutput(DataReceivedEventArgs e) {
                if (_preConnectionOutput == null) {
                    Interlocked.CompareExchange(ref _preConnectionOutput, new StringBuilder(), null);
                }

                lock (_preConnectionOutput) {
                    _preConnectionOutput.Append(FixNewLines(e.Data) + Environment.NewLine);
                }
            }

            public void OutputThread() {
                byte[] cmd_buffer = new byte[4];
                try {
                    _socket = _socket.Accept();
                    using (new SocketLock(this)) {
                        _connected = true;
                        if (_executionFile != null) {
                            SendExecuteFile(_executionFile, _executionExtraArgs);
                            _executionFile = null;
                            _executionExtraArgs = null;
                        }

                        if (_executionText != null) {
                            Debug.WriteLine("Executing delayed text: " + _executionText);
                            SendExecuteText(_executionText);
                            _executionText = null;
                        }
                    }

                    Socket socket;
                    while ((socket = _socket) != null && socket.Receive(cmd_buffer) == 4) {
                        using (new SocketLock(this)) {
                            string cmd = CommandtoString(cmd_buffer);
                            Debug.WriteLine("Repl {0} received command: {1}", _eval.DisplayName, cmd);
                            switch (cmd) {
                                case "DONE": HandleExecutionDone(); break;
                                case "ERRE": HandleExecutionError(); break;
                                case "STDO": HandleOutput(); break;
                                case "STDE": HandleError(); break;
                                case "MERR": HandleMemberResultError(); break;
                                case "MRES": HandleMemberResult(); break;
                                case "SRES": HandleSigResult(); break;
                                case "SERR": HandleSigError(); break;
                                case "MODS": HandleModuleList(); break;
                                case "IMGD": HandleImageDisplay(); break;
                                case "MODC": HandleModulesChanged(); break;
                                case "PRPC": HandlePromptChanged(); break;
                                case "RDLN": HandleReadLine(); break;
                                case "DETC": HandleDebuggerDetach(); break;
                                case "DPNG": DisplayPng(); break;
                                case "EXIT":
                                    // REPL has exited
                                    socket.Send(ExitCommandBytes);
                                    return;
                            }
                        }
                    }
                } catch (SocketException) {
                    _socket = null;
                } catch (DisconnectedException) {
                }
            }

            private Socket Socket {
                get {
#if DEBUG
                    Debug.Assert(_socketLockedThread == Thread.CurrentThread);
#endif
                    for (int i = 0; i < 40 && !_connected; i++) {
                        // wait for connection...
                        System.Threading.Thread.Sleep(100);
                    }

                    var res = _socket;
                    if (res == null) {
                        throw new DisconnectedException("The interactive window has become disconnected from the remote process.  Please reset the window.");
                    }

                    return res;
                }
            }

            class DisconnectedException : Exception {
                public DisconnectedException(string message)
                    : base(message) {
                }
            }

            private void HandleReadLine() {
                // perform the input on a new thread so that we don't block additional commands (such as output) from being processed by us
                // (this is called on the output thread)
                ThreadPool.QueueUserWorkItem(x => {
                    string input = Window.ReadStandardInput();
                    input = input != null ? UnfixNewLines(input) : "\n";
                    try {
                        using (new SocketLock(this)) {
                            Socket.Send(InputLineCommandBytes);
                            SendString(input);
                        }
                    } catch (SocketException) {
                    } catch (DisconnectedException) {
                    }
                });
            }

            private void HandleDebuggerDetach() {
                _eval._attached = false;
            }

            private void DisplayPng() {
                int len = _socket.ReadInt();
                byte[] buffer = new byte[len];
                if (len != 0) {
                    int bytesRead = 0;
                    do {
                        bytesRead += _socket.Receive(buffer, bytesRead, len - bytesRead, SocketFlags.None);
                    } while (bytesRead != len);
                }

                DisplayImage(buffer);
            }

            internal string DoDebugAttach() {
                if (_eval._attached) {
                    return "Cannot attach to debugger when already attached.";
                }

                PythonProcess debugProcess;
                using (new SocketLock(this)) {
                    Socket.Send(DebugAttachCommandBytes);

                    debugProcess = PythonProcess.AttachRepl(_socket, _process.Id, _eval.AnalyzerProjectLanguageVersion);
                }

                // TODO: Surround in SocketUnlock
                var debugTarget = new VsDebugTargetInfo2();
                debugTarget.guidLaunchDebugEngine = AD7Engine.DebugEngineGuid;
                debugTarget.dwDebugEngineCount = 1;

                debugTarget.dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_Custom;
                debugTarget.bstrExe = debugProcess.ProcessGuid.ToString();
                debugTarget.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(VsDebugTargetInfo));
                debugTarget.bstrCurDir = "";
                debugTarget.guidPortSupplier = new Guid("{708C1ECA-FF48-11D2-904F-00C04FA302A1}");     // local port supplier
                debugTarget.LaunchFlags = (uint)__VSDBGLAUNCHFLAGS.DBGLAUNCH_WaitForAttachComplete | (uint)__VSDBGLAUNCHFLAGS5.DBGLAUNCH_BreakOneProcess;
                debugTarget.bstrOptions = AD7Engine.AttachRunning + "=True";
                debugTarget.pDebugEngines = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(Guid)));
                Marshal.StructureToPtr(AD7Engine.DebugEngineGuid, debugTarget.pDebugEngines, false);
                IntPtr memory = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(VsDebugTargetInfo2)));
                Marshal.StructureToPtr(debugTarget, memory, false);
                var debugger = (IVsDebugger2)PythonToolsPackage.GetGlobalService(typeof(SVsShellDebugger));

                int hr = debugger.LaunchDebugTargets2(1, memory);
                if (ErrorHandler.Failed(hr)) {
                    var uiShell = (IVsUIShell)PythonToolsPackage.GetGlobalService(typeof(SVsUIShell));
                    string errorText;
                    uiShell.GetErrorInfo(out errorText);
                    if (String.IsNullOrWhiteSpace(errorText)) {
                        errorText = "Unknown Error: " + hr;
                    }
                    return errorText;
                } else {
                    _eval._attached = true;
                }

                GC.KeepAlive(debugProcess);
                return null;
            }

            /// <summary>
            /// Replaces \r\n with \n
            /// </summary>
            private string UnfixNewLines(string input) {
                StringBuilder res = new StringBuilder();
                for (int i = 0; i < input.Length; i++) {
                    if (input[i] == '\r' && i != input.Length - 1 && input[i + 1] == '\n') {
                        res.Append('\n');
                        i++;
                    } else {
                        res.Append(input[i]);
                    }
                }
                return res.ToString();
            }

            private void HandlePromptChanged() {
                // prompt change
                _prompt1 = Socket.ReadString();
                _prompt2 = Socket.ReadString();
                bool updateAll = Socket.ReadInt() == 1;
                if (Window != null) {
                    using (new SocketUnlock(this)) {
                        _eval.UpdatePrompts(updateAll);
                    }
                }
            }

            private void HandleModulesChanged() {
                // modules changed
                using (new SocketUnlock(this)) {
                    var curScopesChanged = _eval.AvailableScopesChanged;
                    if (curScopesChanged != null) {
                        curScopesChanged(this, EventArgs.Empty);
                    }
                }
            }

            private void HandleImageDisplay() {
                string filename = Socket.ReadString();
                try {
                    DisplayImage(File.ReadAllBytes(filename));
                } catch (IOException) {
                    // can't read the file
                    Window.WriteError("Unable to read image file " + filename);
                }

            }

            private void DisplayImage(byte[] bytes) {
                using (new SocketUnlock(this)) {
                    ((System.Windows.UIElement)Window.TextView).Dispatcher.BeginInvoke((Action)(() => {
                        try {
                            var imageSrc = new BitmapImage();
                            imageSrc.BeginInit();
                            imageSrc.StreamSource = new MemoryStream(bytes);
                            imageSrc.EndInit();

                            Window.WriteOutput(new Image() { Source = imageSrc });
                        } catch (IOException) {
                        }
                    }));
                }
            }

            private void HandleModuleList() {
                int moduleCount = Socket.ReadInt();
                Dictionary<string, string> moduleNames = new Dictionary<string, string>();
                Dictionary<string, bool> allModules = new Dictionary<string, bool>();
                for (int i = 0; i < moduleCount; i++) {
                    string name = Socket.ReadString();
                    string filename = Socket.ReadString();
                    if (!String.IsNullOrWhiteSpace(filename)) {
                        moduleNames[filename] = name;
                        allModules[name] = true;
                    } else {
                        allModules[name] = false;
                    }
                }

                _fileToModuleName = moduleNames;
                _allModules = allModules;
                _completionResultEvent.Set();
            }

            private void HandleSigError() {
                _completionResultEvent.Set();
            }

            private void HandleSigResult() {
                int overloadCount = Socket.ReadInt();
                OverloadDoc[] docs = new OverloadDoc[overloadCount];
                for (int i = 0; i < overloadCount; i++) {
                    string doc = Socket.ReadString();
                    int paramCount = Socket.ReadInt();

                    ParameterResult[] parameters = new ParameterResult[paramCount];
                    for (int curParam = 0; curParam < paramCount; curParam++) {
                        string name = Socket.ReadString();
                        parameters[curParam] = new ParameterResult(name);
                    }
                    docs[i] = new OverloadDoc(doc, parameters);
                }
                _overloads = docs;
                _completionResultEvent.Set();
            }

            private void HandleMemberResult() {
                string typeName = Socket.ReadString();

                var instDict = ReadMemberDict();
                var typeDict = ReadMemberDict();
                _memberResults = new MemberResults(typeName, instDict, typeDict);

                _completionResultEvent.Set();
            }

            private void HandleMemberResultError() {
                _memberResults = null;
                _completionResultEvent.Set();
            }

            private void HandleOutput() {
                string data = Socket.ReadString();
                if (data != null) {
                    using (new SocketUnlock(this)) {
                        Window.WriteOutput(FixNewLines(data));
                    }
                }
            }

            private void HandleError() {
                string data = Socket.ReadString();
                using (new SocketUnlock(this)) {
                    Window.WriteError(FixNewLines(data));
                }
            }

            private void HandleExecutionError() {
                using (new SocketUnlock(this)) {
                    // DONE command
                    if (_completion != null) {
                        _completion.SetResult(ExecutionResult.Failure);
                        _completion = null;
                    }
                }
            }

            private void HandleExecutionDone() {
                using (new SocketUnlock(this)) {
                    // DONE command
                    if (_completion != null) {
                        _completion.SetResult(ExecutionResult.Success);
                        _completion = null;
                    }
                }
            }

            static string _noReplProcess = "Current interactive window is disconnected - please reset the process." + Environment.NewLine;
            public Task<ExecutionResult> ExecuteText(string text) {
                if (text.StartsWith("$")) {
                    _eval._window.WriteError(String.Format("Unknown command '{0}', use \"$help\" for help" + Environment.NewLine, text.Substring(1).Trim()));
                    return ExecutionResult.Failed;
                }

                Debug.WriteLine("Executing text: " + text);
                using (new SocketLock(this)) {
                    if (!_connected) {
                        // delay executing the text until we're connected
                        Debug.WriteLine("Delayed executing text");
                        _completion = new TaskCompletionSource<ExecutionResult>();
                        _executionText = text;
                        return _completion.Task;
                    }

                    try {
                        if (!Socket.Connected) {
                            _eval._window.WriteError(_noReplProcess);
                            return ExecutionResult.Failed;
                        }

                        _completion = new TaskCompletionSource<ExecutionResult>();

                        SendExecuteText(text);
                    } catch (DisconnectedException) {
                        _eval._window.WriteError(_noReplProcess);
                        return ExecutionResult.Failed;
                    } catch (SocketException) {
                        _eval._window.WriteError(_noReplProcess);
                        return ExecutionResult.Failed;
                    }

                    return _completion.Task;
                }
            }

            [DllImport("user32", CallingConvention = CallingConvention.Winapi)]
            static extern bool AllowSetForegroundWindow(int dwProcessId);

            private void SendExecuteText(string text) {
                AllowSetForegroundWindow(_process.Id);

                Socket.Send(RunCommandBytes);

                // normalize line endings to \n which is all older versions of CPython can handle.
                text = text.Replace("\r\n", "\n");
                text = text.Replace("\r", "\n");
                text = text.TrimEnd(' ');
                SendString(text);
            }

            public void ExecuteFile(string filename, string extraArgs) {
                using (new SocketLock(this)) {
                    if (!_connected) {
                        // delay executing the text until we're connected
                        _executionFile = filename;
                        _executionExtraArgs = extraArgs;
                        return;
                    } else if (!Socket.Connected) {
                        _eval._window.WriteError(_noReplProcess);
                        return;
                    }

                    SendExecuteFile(filename, extraArgs);
                }
            }

            private void SendExecuteFile(string filename, string extraArgs) {
                AllowSetForegroundWindow(_process.Id);

                Socket.Send(ExecuteFileCommandBytes);
                SendString(filename);
                SendString(extraArgs ?? String.Empty);
            }

            public void AbortCommand() {
                using (new SocketLock(this)) {
                    Socket.Send(AbortCommandBytes);
                }
            }

            public void SetThreadAndFrameCommand(int thread, int frame, FrameKind frameKind) {
                using (new SocketLock(this)) {
                    Socket.Send(SetThreadAndFrameCommandBytes);
                    Socket.Send(BitConverter.GetBytes(thread));
                    Socket.Send(BitConverter.GetBytes(frame));
                    Socket.Send(BitConverter.GetBytes((int)frameKind));
                    _currentScope = "<CurrentFrame>";
                }
            }

            public OverloadDoc[] GetSignatureDocumentation(string text) {
                using (new SocketLock(this)) {
                    if (!Socket.Connected || !_connected) {
                        return new OverloadDoc[0];
                    }
                    try {
                        Socket.Send(GetSignaturesCommandBytes);
                        SendString(text);
                    } catch (SocketException) {
                        return new OverloadDoc[0];
                    }
                }

                if (_completionResultEvent.WaitOne(1000)) {
                    var res = _overloads;
                    _overloads = null;
                    return res;
                }
                return null;
            }

            public MemberResult[] GetMemberNames(string text) {
                _completionResultEvent.Reset();
                _memberResults = null;

                using (new SocketLock(this)) {
                    if (!Socket.Connected || !_connected) {
                        return new MemberResult[0];
                    }
                    try {
                        Socket.Send(GetMembersCommandBytes);
                        SendString(text);
                    } catch (SocketException) {
                        return new MemberResult[0];
                    }
                }

                if (_completionResultEvent.WaitOne(1000) && _memberResults != null) {
                    MemberResult[] res = new MemberResult[_memberResults.TypeMembers.Count + _memberResults.InstanceMembers.Count];
                    int i = 0;
                    foreach (var member in _memberResults.TypeMembers) {
                        res[i++] = CreateMemberResult(member.Key, member.Value);
                    }
                    foreach (var member in _memberResults.InstanceMembers) {
                        res[i++] = CreateMemberResult(member.Key, member.Value);
                    }

                    _memberResults = null;
                    return res;
                }
                return null;
            }

            public string GetScopeByFilename(string path) {
                GetAvailableScopesAndKind();

                string res;
                if (_fileToModuleName.TryGetValue(path, out res)) {
                    return res;
                }
                return null;
            }

            public void SetScope(string scopeName) {
                try {
                    using (new SocketLock(this)) {
                        Socket.Send(SetModuleCommandBytes);
                        SendString(scopeName);
                        _currentScope = scopeName;

                        _eval._window.WriteLine(String.Format("Current module changed to {0}", scopeName));
                    }
                } catch (SocketException) {
                    _eval._window.WriteError("Cannot change module, interactive window is disconnected.");
                }
            }

            public IEnumerable<string> GetAvailableUserScopes() {
                if (_connected) {   // if startup's taking a long time we won't be connected yet
                    using (new SocketLock(this)) {
                        Socket.Send(GetModulesListCommandBytes);
                    }

                    _completionResultEvent.WaitOne(1000);

                    if (_fileToModuleName != null) {
                        return _fileToModuleName.Values;
                    }
                }
                return new string[0];
            }

            public IEnumerable<KeyValuePair<string, bool>> GetAvailableScopesAndKind() {
                if (_connected) {   // if startup's taking a long time we won't be connected yet
                    using (new SocketLock(this)) {
                        Socket.Send(GetModulesListCommandBytes);
                    }

                    _completionResultEvent.WaitOne(1000);

                    if (_allModules != null) {
                        return _allModules;
                    }
                }
                return new KeyValuePair<string, bool>[0];
            }

            public void Close() {
                // try and exit gracefully first, but if we're wedged don't both...
                if (Monitor.TryEnter(_socketLock, 200)) {
                    try {
                        using (new SocketLock(this)) {
                            if (_socket != null && _socket.Connected) {
                                var socket = Socket;
                                _socket = null;

                                try {
                                    socket.Send(ExitCommandBytes);
                                    socket.Close();
                                } catch (SocketException) {
                                }
                            }
                        }
                    } finally {
                        Monitor.Exit(_socketLock);
                    }
                }

                if (!_process.HasExited) {
                    try {
                        _process.Kill();
                    } catch (InvalidOperationException) {
                        // race w/ killing the process
                    }
                }

                if (_completion != null) {
                    _completion.SetResult(ExecutionResult.Failure);
                }
            }

            private void SendString(string text) {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
                Socket.Send(BitConverter.GetBytes(bytes.Length));
                Socket.Send(bytes);
            }

            private Dictionary<string, string> ReadMemberDict() {
                int memCount = Socket.ReadInt();
                var dict = new Dictionary<string, string>(memCount);
                for (int i = 0; i < memCount; i++) {
                    string memName = Socket.ReadString();
                    string typeName = Socket.ReadString();
                    dict[memName] = typeName;
                }

                return dict;
            }

            private IReplWindow Window {
                get {
                    return _eval._window;
                }
            }

            /// <summary>
            /// Helper struct for locking and tracking the current holding thread.  This allows
            /// us to assert that our socket is always accessed while the lock is held.  The lock
            /// needs to be held so that requests from the UI (switching scopes, getting module lists,
            /// executing text, etc...) won't become interleaved with interactions from the repl process 
            /// (output, execution completing, etc...).
            /// </summary>
            struct SocketLock : IDisposable {
                private readonly ListenerThread _evaluator;

                public SocketLock(ListenerThread evaluator) {
                    Monitor.Enter(evaluator._socketLock);
#if DEBUG
                    Debug.Assert(evaluator._socketLockedThread == null);
                    evaluator._socketLockedThread = Thread.CurrentThread;
#endif
                    _evaluator = evaluator;
                }

                public void Dispose() {
#if DEBUG
                    _evaluator._socketLockedThread = null;
#endif
                    Monitor.Exit(_evaluator._socketLock);
                }
            }

            /// <summary>
            /// Releases the socket lock and re-acquires it when finished.  This enables
            /// calling back into the repl window which could potentially call back to do
            /// work w/ the evaluator that we don't want to deadlock.
            /// </summary>
            struct SocketUnlock : IDisposable {
                private readonly ListenerThread _evaluator;

                public SocketUnlock(ListenerThread evaluator) {
#if DEBUG
                    Debug.Assert(evaluator._socketLockedThread == Thread.CurrentThread);
                    evaluator._socketLockedThread = null;
#endif
                    _evaluator = evaluator;
                    Monitor.Exit(evaluator._socketLock);
                }

                public void Dispose() {
                    Monitor.Enter(_evaluator._socketLock);
#if DEBUG
                    _evaluator._socketLockedThread = Thread.CurrentThread;
#endif
                }
            }

        }

        private static string CommandtoString(byte[] cmd_buffer) {
            return new string(new char[] { (char)cmd_buffer[0], (char)cmd_buffer[1], (char)cmd_buffer[2], (char)cmd_buffer[3] });
        }


        private void UpdatePrompts(bool updateAll) {
            if (CurrentOptions.UseInterpreterPrompts && _curListener != null) {
                _window.SetOptionValue(updateAll ? ReplOptions.PrimaryPrompt : ReplOptions.CurrentPrimaryPrompt, _curListener._prompt1);
                _window.SetOptionValue(updateAll ? ReplOptions.SecondaryPrompt : ReplOptions.CurrentSecondaryPrompt, _curListener._prompt2);
            } else {
                _window.SetOptionValue(updateAll ? ReplOptions.PrimaryPrompt : ReplOptions.CurrentPrimaryPrompt, CurrentOptions.PrimaryPrompt);
                _window.SetOptionValue(updateAll ? ReplOptions.SecondaryPrompt : ReplOptions.CurrentSecondaryPrompt, CurrentOptions.SecondaryPrompt);
            }
        }

        /// <summary>
        /// Transforms lone \r or \n into \r\n.
        /// </summary>
        private static string FixNewLines(string output) {
            StringBuilder fixedOutput = new StringBuilder();
            for (int i = 0; i < output.Length; i++) {
                if (output[i] == '\r' && i < output.Length - 1) {
                    if (output[i + 1] == '\n') {
                        i++;
                        if (fixedOutput != null) {
                            fixedOutput.Append("\r\n");
                        }
                        continue;
                    } else {
                        // single \r, change to \r\n
                        if (fixedOutput == null) {
                            fixedOutput = new StringBuilder(output, 0, i, output.Length + 128);
                        }
                        fixedOutput.Append("\r\n");
                    }
                } else if (output[i] == '\n') {
                    // single \n, change to \r\n
                    if (fixedOutput == null) {
                        fixedOutput = new StringBuilder(output, 0, i, output.Length + 128);
                    }
                    fixedOutput.Append("\r\n");
                } else if (fixedOutput != null) {
                    // normal char, and we've already transformed a \n or \r to \r\n
                    fixedOutput.Append(output[i]);
                }
            }
            return fixedOutput.ToString();
        }

        class MemberResults {
            public readonly string TypeName;
            public readonly Dictionary<string, string> InstanceMembers;
            public readonly Dictionary<string, string> TypeMembers;

            public MemberResults(string typeName, Dictionary<string, string> instMembers, Dictionary<string, string> typeMembers) {
                TypeName = typeName;
                InstanceMembers = instMembers;
                TypeMembers = typeMembers;
            }

        }
        private static bool IsCommand(byte[] cmd_buffer, string command) {
            return cmd_buffer[0] == command[0] && cmd_buffer[1] == command[1] && cmd_buffer[2] == command[2] && cmd_buffer[3] == command[3];
        }

        public bool CanExecuteText(string text) {
            int newLines = 0;
            for (int i = text.Length - 1; i >= 0; i--) {
                if (text[i] == '\n') {
                    if (++newLines == 1) {
                        return true;
                    }
                } else if (Char.IsWhiteSpace(text[i])) {
                    continue;
                } else {
                    break;
                }
            }

            var parser = Parser.CreateParser(new StringReader(text), LanguageVersion);
            ParseResult result;
            parser.ParseInteractiveCode(out result);
            if (result == ParseResult.Empty) {
                return false;
            } else if (!(result == ParseResult.Complete || result == ParseResult.Invalid)) {
                return false;
            }

            // Single-line: if it's executable, then execute
            if (text.IndexOf('\n') == -1) {
                return true;
            }

            return false;
        }

        private static byte[] MakeCommand(string command) {
            return new byte[] { (byte)command[0], (byte)command[1], (byte)command[2], (byte)command[3] };
        }

        public Task<ExecutionResult> ExecuteText(string text) {
            var res = _lastExecutionResult = ExecuteTextWorker(text);
            return res;
        }

        private Task<ExecutionResult> ExecuteTextWorker(string text) {
            var parser = Parser.CreateParser(new StringReader(text), LanguageVersion);
            ParseResult parseResult;
            parser.ParseInteractiveCode(out parseResult);
            if (parseResult == ParseResult.Empty) {
                return ExecutionResult.Succeeded;
            }

            EnsureConnected();
            if (_curListener != null) {
                return _curListener.ExecuteText(text);
            } else {
                _window.WriteError("Current interactive window is disconnected." + Environment.NewLine);
            }
            return ExecutionResult.Failed;
        }

        public void ExecuteFile(string filename) {
            EnsureConnected();

            string startupFilename, startupDir, extraArgs = null;
            VsProjectAnalyzer analyzer;
            if (PythonToolsPackage.TryGetStartupFileAndDirectory(out startupFilename, out startupDir, out analyzer)) {
                var startupProj = PythonToolsPackage.GetStartupProject();
                if (startupProj != null) {
                    extraArgs = startupProj.GetProjectProperty(CommonConstants.CommandLineArguments, true);
                }
            }

            if (_curListener != null) {
                _curListener.ExecuteFile(filename, extraArgs);
            } else {
                _window.WriteError("Current interactive window is disconnected." + Environment.NewLine);
            }
        }

        public void AbortCommand() {
            if (_curListener != null) {
                _curListener.AbortCommand();
            }
        }

        public void SetThreadAndFrameCommand(int thread, int frame, FrameKind frameKind) {
            if (_curListener != null) {
                _curListener.SetThreadAndFrameCommand(thread, frame, frameKind);
            }
        }

        public Task<ExecutionResult> Reset() {
            // suppress reporting "failed to launch repl" process
            if (_curListener == null) {
                _window.WriteError("Interactive window is not yet started." + Environment.NewLine);
                return ExecutionResult.Succeeded;
            }

            _curListener._connected = true;

            Close();
            Connect();

            BufferParser parser = null;

            var buffersBeforeReset = _window.TextView.BufferGraph.GetTextBuffers(TruePredicate);
            for (int i = 0; i < buffersBeforeReset.Count - 1; i++) {
                var buffer = buffersBeforeReset[i];

                if (!buffer.Properties.ContainsProperty(InputBeforeReset)) {
                    buffer.Properties.AddProperty(InputBeforeReset, InputBeforeReset);
                }

                if (parser == null) {
                    buffer.Properties.TryGetProperty<BufferParser>(typeof(BufferParser), out parser);
                }
            }
            if (parser != null) {
                parser.Requeue();
            }

            return ExecutionResult.Succeeded;
        }

        private static bool TruePredicate(ITextBuffer buffer) {
            return true;
        }

        const string _splitRegexPattern = @"(?x)\s*,\s*(?=(?:[^""]*""[^""]*"")*[^""]*$)"; // http://regexhero.net/library/52/
        private static Regex _splitLineRegex = new Regex(_splitRegexPattern);

        public string FormatClipboard() {
            if (Clipboard.ContainsData(DataFormats.CommaSeparatedValue)) {
                string data = Clipboard.GetData(DataFormats.CommaSeparatedValue) as string;
                if (data != null) {
                    string[] lines = data.Split(new[] { "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                    StringBuilder res = new StringBuilder();
                    res.AppendLine("[");
                    foreach (var line in lines) {
                        string[] items = _splitLineRegex.Split(line);

                        res.Append("  [");
                        for (int i = 0; i < items.Length; i++) {
                            res.Append(FormatItem(items[i]));

                            if (i != items.Length - 1) {
                                res.Append(", ");
                            }
                        }
                        res.AppendLine("],");
                    }
                    res.AppendLine("]");
                    return res.ToString();
                }
            }
            return EditFilter.RemoveReplPrompts(_window.TextView.Options.GetNewLineCharacter());
        }

        private static string FormatItem(string item) {
            if (String.IsNullOrWhiteSpace(item)) {
                return "None";
            }
            double doubleVal;
            int intVal;
            if (Double.TryParse(item, out doubleVal) ||
                Int32.TryParse(item, out intVal)) {
                return item;
            }

            if (item[0] == '"' && item[item.Length - 1] == '"' && item.IndexOf(',') != -1) {
                // remove outer quotes, remove "" escaping
                item = item.Substring(1, item.Length - 2).Replace("\"\"", "\"");
            }

            // put in single quotes and escape single quotes and backslashes
            return "'" + item.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        }

        #endregion

        #region IDisposable Members

        public void Dispose() {
            try {
                Close();
            } catch {
            }
        }

        protected virtual void Close() {
            if (_curListener != null) {
                _curListener.Close();
            }
            _attached = false;
        }

        #endregion

        #region IPythonReplIntellisense Members

        public bool LiveCompletionsOnly {
            get {
                return CurrentOptions.LiveCompletionsOnly;
            }
        }

        public MemberResult[] GetMemberNames(string text) {
            EnsureConnected();

            return _curListener.GetMemberNames(text);
        }

        public OverloadDoc[] GetSignatureDocumentation(string text) {
            EnsureConnected();

            return _curListener.GetSignatureDocumentation(text);
        }

        public IEnumerable<KeyValuePair<string, bool>> GetAvailableScopesAndKind() {
            if (_curListener != null) {
                return _curListener.GetAvailableScopesAndKind();
            }

            return new KeyValuePair<string, bool>[0];
        }

        #endregion

        private static MemberResult CreateMemberResult(string name, string typeName) {
            switch (typeName) {
                case "__builtin__.method-wrapper":
                case "__builtin__.builtin_function_or_method":
                case "__builtin__.method_descriptor":
                case "__builtin__.wrapper_descriptor":
                case "__builtin__.instancemethod":
                    return new MemberResult(name, PythonMemberType.Method);
                case "__builtin__.getset_descriptor":
                    return new MemberResult(name, PythonMemberType.Property);
                case "__builtin__.namespace#":
                    return new MemberResult(name, PythonMemberType.Namespace);
                case "__builtin__.type":
                    return new MemberResult(name, PythonMemberType.Class);
                case "__builtin__.function":
                    return new MemberResult(name, PythonMemberType.Function);
                case "__builtin__.module":
                    return new MemberResult(name, PythonMemberType.Module);
            }

            return new MemberResult(name, PythonMemberType.Field);
        }

        #region IMultipleScopeEvaluator Members

        public IEnumerable<string> GetAvailableScopes() {
            if (_curListener != null) {
                return _curListener.GetAvailableUserScopes();
            }
            return new string[0];
        }

        public event EventHandler<EventArgs> AvailableScopesChanged;
        public event EventHandler<EventArgs> MultipleScopeSupportChanged;

        public void SetScope(string scopeName) {
            EnsureConnected();

            _curListener.SetScope(scopeName);
        }

        public string CurrentScopeName {
            get {
                if (_curListener != null) {
                    return _curListener._currentScope;
                }
                return "<disconnected>";
            }
        }

        public bool EnableMultipleScopes {
            get { return _multipleScopes; }
        }

        #endregion

        internal string GetScopeByFilename(string path) {
            if (_curListener != null) {
                return _curListener.GetScopeByFilename(path);
            }
            return null;
        }

        public string PrimaryPrompt {
            get {
                if (_curListener != null) {
                    return _curListener._prompt1;
                }
                return ">>> ";
            }
        }

        public string SecondaryPrompt {
            get {
                if (_curListener != null) {
                    return _curListener._prompt2;
                }
                return "... ";
            }
        }

        internal string AttachDebugger() {
            EnsureConnected();

            return _curListener.DoDebugAttach();
        }

        internal IEnumerable<string> SplitCode(string code) {
            var lines = code.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            StringBuilder temp = new StringBuilder();
            string prevText = null;
            ParseResult? prevParseResult = null;
            for (int i = 0; i < lines.Length; i++) {
                var line = lines[i];

                if (i == lines.Length - 1) {
                    temp.Append(line);
                } else {
                    temp.AppendLine(line);
                }
                string newCode = temp.ToString();

                var parser = Parser.CreateParser(new StringReader(newCode), LanguageVersion);
                ParseResult result;
                parser.ParseInteractiveCode(out result);

                // if this parse is invalid then we need more text to be valid.
                // But if this text is invalid and the previous parse was incomplete
                // then appending more text won't fix things - the code in invalid, the user
                // needs to fix it, so let's not break it up which would prevent that from happening.
                if (result == ParseResult.Empty) {
                    if (!String.IsNullOrWhiteSpace(newCode)) {
                        // comment line, include w/ following code.
                        prevText = newCode;
                        prevParseResult = result;
                    } else {
                        temp.Clear();
                    }
                } else if (result == ParseResult.Complete) {
                    yield return FixEndingNewLine(newCode);
                    temp.Clear();

                    prevParseResult = null;
                    prevText = null;
                } else if (ShouldAppendCode(prevParseResult, result)) {
                    prevText = newCode;
                    prevParseResult = result;
                } else if (prevText != null) {
                    // we have a complete input
                    yield return FixEndingNewLine(prevText);
                    temp.Clear();

                    // reparse this line so our state remains consistent as if we just started out.
                    i--;
                    prevParseResult = null;
                    prevText = null;
                } else {
                    prevParseResult = result;
                }
            }

            if (temp.Length > 0) {
                yield return FixEndingNewLine(temp.ToString());
            }
        }

        private static bool ShouldAppendCode(ParseResult? prevParseResult, ParseResult result) {
            if (result == ParseResult.Invalid) {
                if (prevParseResult == ParseResult.IncompleteStatement || prevParseResult == ParseResult.Invalid) {
                    return false;
                }
            }
            return true;
        }

        public IReplWindow Window {
            get {
                return _window;
            }
            set {
                _window = value;
            }
        }

        private static string FixEndingNewLine(string prevText) {
            if ((prevText.IndexOf('\n') == prevText.LastIndexOf('\n')) &&
                (prevText.IndexOf('\r') == prevText.LastIndexOf('\r'))) {
                prevText = prevText.TrimEnd();
            } else if (prevText.EndsWith("\r\n\r\n")) {
                prevText = prevText.Substring(0, prevText.Length - 2);
            } else if (prevText.EndsWith("\n\n") || prevText.EndsWith("\r\r")) {
                prevText = prevText.Substring(0, prevText.Length - 1);
            }
            return prevText;
        }
    }
}