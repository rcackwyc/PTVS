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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DebuggerTests;
using Microsoft.PythonTools;
using Microsoft.PythonTools.Debugger;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestUtilities;

namespace DjangoTests {
    [TestClass]
    public class DjangoDebuggerTests : BaseDebuggerTests {
        private static DbState _dbstate;

        enum DbState {
            Unknown,
            BarApp
        }

        [ClassInitialize]
        public static void DoDeployment(TestContext context) {
            TestData.Deploy();
            
            var dbFile = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "DjangoProjectDatabase.db");
            if (File.Exists(dbFile)) {
                File.Delete(dbFile);
            }
        }

        /// <summary>
        /// Ensures the app is initialized with the appropriate set of data.  If we're
        /// already initialized that way we don't re-initialize.
        /// </summary>
        private void Init(DbState requiredState) {
            if (_dbstate != requiredState) {
                switch (requiredState) {
                    case DbState.BarApp:
                        using (var output = ProcessOutput.Run(Version.Path,
                            new [] {"manage.py", "syncdb", "--noinput"},
                            Path.Combine(Environment.CurrentDirectory, DebuggerTestPath),
                            null, false, null)) {
                            output.Wait();
                            Console.WriteLine(" ** stdout **");
                            Console.WriteLine(string.Join(Environment.NewLine, output.StandardOutputLines));
                            Console.WriteLine(" ** stderr **");
                            Console.WriteLine(string.Join(Environment.NewLine, output.StandardErrorLines));
                            Assert.AreEqual(0, output.ExitCode);
                        }

                        using (var output = ProcessOutput.Run(Version.Path,
                            new [] {"manage.py", "loaddata", "data.json"},
                            Path.Combine(Environment.CurrentDirectory, DebuggerTestPath),
                            null, false, null)) {
                            output.Wait();
                            Console.WriteLine(" ** stdout **");
                            Console.WriteLine(string.Join(Environment.NewLine, output.StandardOutputLines));
                            Console.WriteLine(" ** stderr **");
                            Console.WriteLine(string.Join(Environment.NewLine, output.StandardErrorLines));
                            Assert.AreEqual(0, output.ExitCode);
                        }
                        break;
                }
                _dbstate = requiredState;
            }
        }

        private void OutputDataReceived(object sender, DataReceivedEventArgs e) {
            if (e != null) {
                Console.WriteLine("Output: {0}", e.Data);
            }
        }

        [TestMethod, Priority(0)]
        public void TemplateStepping() {
            Init(DbState.BarApp);

            StepTest(
                Path.Combine(Environment.CurrentDirectory, DebuggerTestPath, "manage.py"),
                Path.Combine(Environment.CurrentDirectory, DebuggerTestPath, "Templates\\polls\\loop.html"),
                "runserver --noreload",
                new[] { 1 }, // break on line 1,
                new Action<PythonProcess>[] { x => {  } },
                new WebPageRequester("http://127.0.0.1:8000/loop/").DoRequest,
                PythonDebugOptions.DjangoDebugging,
                false,
                new ExpectedStep(StepKind.Resume, 2),     // first line in manage.py
                new ExpectedStep(StepKind.Over, 1),     // step over for
                new ExpectedStep(StepKind.Over, 2),     // step over {{ color }}
                new ExpectedStep(StepKind.Over, 3),     // step over {{ color }}
                new ExpectedStep(StepKind.Over, 2),     // step over {{ color }}
                new ExpectedStep(StepKind.Over, 3),     // step over {{ color }}
                new ExpectedStep(StepKind.Over, 2),     // step over {{ color }}
                new ExpectedStep(StepKind.Resume, 3)     // step over {{ color }}
            );

            StepTest(
                Path.Combine(Environment.CurrentDirectory, DebuggerTestPath, "manage.py"),
                Path.Combine(Environment.CurrentDirectory, DebuggerTestPath, "Templates\\polls\\loop_nobom.html"),
                "runserver --noreload",
                new[] { 1 }, // break on line 1,
                new Action<PythonProcess>[] { x => { } },
                new WebPageRequester("http://127.0.0.1:8000/loop_nobom/").DoRequest,
                PythonDebugOptions.DjangoDebugging,
                false,
                new ExpectedStep(StepKind.Resume, 2),     // first line in manage.py
                new ExpectedStep(StepKind.Over, 1),     // step over for
                new ExpectedStep(StepKind.Over, 2),     // step over {{ color }}
                new ExpectedStep(StepKind.Over, 3),     // step over {{ color }}
                new ExpectedStep(StepKind.Over, 2),     // step over {{ color }}
                new ExpectedStep(StepKind.Over, 3),     // step over {{ color }}
                new ExpectedStep(StepKind.Over, 2),     // step over {{ color }}
                new ExpectedStep(StepKind.Resume, 3)     // step over {{ color }}
            );
        }

        [TestMethod, Priority(0)]
        public void BreakInTemplate() {
            Init(DbState.BarApp);

            string cwd = Path.Combine(Environment.CurrentDirectory, DebuggerTestPath);
            
            BreakpointTest(
                "manage.py",
                new[] { 1, 3, 4 },
                new[] { 1, 3, 4 },
                breakFilename: Path.Combine(cwd, "Templates", "polls", "index.html"),
                arguments: "runserver --noreload",
                checkBound: false,
                checkThread: false,
                processLoaded: new WebPageRequester().DoRequest,
                debugOptions: PythonDebugOptions.DjangoDebugging,
                waitForExit: false
            );
        }

        [TestMethod, Priority(0)]
        public void TemplateLocals() {
            Init(DbState.BarApp);

            LocalsTest("polls\\index.html", 3, new[] { "latest_poll_list" });
            LocalsTest("polls\\index.html", 4, new[] { "forloop", "latest_poll_list", "poll" });
        }

        private void LocalsTest(string filename, int breakLine, string[] expectedLocals) {
            string cwd = Path.Combine(Environment.CurrentDirectory, DebuggerTestPath);
            LocalsTest("manage.py",
                breakLine,
                new string[0],
                expectedLocals,
                breakFilename: Path.Combine(cwd, "Templates", filename),
                arguments: "runserver --noreload",
                processLoaded: new WebPageRequester().DoRequest,
                debugOptions: PythonDebugOptions.DjangoDebugging,
                waitForExit: false
            );
        }

        class WebPageRequester {
            private readonly string _url;

            public WebPageRequester(string url = "http://127.0.0.1:8000/Bar/") {
                _url = url;
            }

            public void DoRequest() {
                ThreadPool.QueueUserWorkItem(DoRequestWorker, null);
            }

            public void DoRequestWorker(object data) {
                Console.WriteLine("Waiting for port to open...");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Blocking = true;
                for (int i = 0; i < 200; i++) {
                    try {
                        socket.Connect(IPAddress.Loopback, 8000);
                        break;
                    } catch {
                        System.Threading.Thread.Sleep(1000);
                    }
                }
                socket.Close();

                Console.WriteLine("Requesting {0}", _url);
                HttpWebRequest myReq = (HttpWebRequest)WebRequest.Create(_url);
                try {
                    var response = myReq.GetResponse();
                    using (var stream = response.GetResponseStream()) {
                        Console.WriteLine("Response: {0}", new StreamReader(stream).ReadToEnd());
                    }
                } catch (WebException) {
                    // the process can be killed and the connection with it
                }
            }
        }

        internal override string DebuggerTestPath {
            get {
                return TestData.GetPath(@"TestData\DjangoProject\");
            }
        }

        private PythonVersion _version;
        internal override PythonVersion Version {
            get {
                if (_version == null) {
                    _version = PythonPaths.Versions.Reverse().FirstOrDefault(v =>
                        v != null &&
                        Directory.Exists(v.LibPath) &&
                        Directory.EnumerateDirectories(Path.Combine(v.LibPath, "site-packages")).Any(d =>
                            Path.GetFileName(d).StartsWith("django")
                        )
                    );
                }
                return _version;
            }
        }

    }
}