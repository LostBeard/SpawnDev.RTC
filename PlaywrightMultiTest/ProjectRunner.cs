using Microsoft.Playwright;
using SpawnDev.UnitTesting;
using System.Diagnostics;
using System.Text.Json;

namespace PlaywrightMultiTest
{
    public class ProjectRunner
    {
        public static ProjectRunner Instance => GetRunner().GetAwaiter().GetResult()!;
        private static Task<ProjectRunner>? _projectRunner;
        public List<TestableProject> TestableProjects { get; } = new List<TestableProject>();

        /// <summary>
        /// Returns an initialized ProjectRunner singleton
        /// </summary>
        /// <returns></returns>
        static Task<ProjectRunner> GetRunner() => _projectRunner ??= new Func<Task<ProjectRunner>>(async () =>
        {
            var ret = new ProjectRunner();
            await ret.Init().ConfigureAwait(false);
            return ret;
        })();

        /// <summary>
        /// Private consturoctor to prevent external instantiation. The runner should only be created through the GetRunner property which ensures proper initialization.
        /// </summary>
        private ProjectRunner() { }

        private static async Task<int> RunDotnetAsync(string args, string workingDir, int timeoutMs = 300000)
        {
            LogStatus($"RunDotnetAsync: dotnet {args.Split(' ')[0]} (timeout={timeoutMs/1000}s)");
            var startInfo = new ProcessStartInfo("dotnet", args)
            {
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = new Process();
            p.StartInfo = startInfo;
            p.EnableRaisingEvents = true;

            // Use event-based async reads to avoid pipe buffer deadlocks
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };

            var exitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            p.Exited += (_, _) => exitTcs.TrySetResult(true);

            p.Start();
            LogStatus($"RunDotnetAsync: started PID={p.Id}");
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // Wait for exit or timeout
            using var cts = new CancellationTokenSource(timeoutMs);
            using var reg = cts.Token.Register(() => exitTcs.TrySetResult(false));
            var exited = await exitTcs.Task.ConfigureAwait(false);

            if (exited)
            {
                // WaitForExit() with no args can hang if child processes still hold
                // redirected stream handles. Use a short timed wait instead.
                p.WaitForExit(5000);
                LogStatus($"RunDotnetAsync: done PID={p.Id} exit={p.ExitCode}");
                return p.ExitCode;
            }
            else
            {
                LogStatus($"RunDotnetAsync: TIMEOUT after {timeoutMs / 1000}s, killing PID={p.Id}...");
                try { p.Kill(entireProcessTree: true); } catch { }
                return -1;
            }
        }
        /// <summary>
        /// Async initialization method for the ProjectRunner. This is where you can perform any setup that needs to happen before tests are enumerated, such as reading configuration files, setting up logging, etc.
        /// </summary>
        /// <returns></returns>
        // Status file for diagnosing startup hangs
        private static readonly string StatusFile = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "init_status.log");
        private static void LogStatus(string msg)
        {
            var tid = Environment.CurrentManagedThreadId;
            var isPool = Thread.CurrentThread.IsThreadPoolThread;
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [T{tid}{(isPool ? ",pool" : "")}] {msg}";
            Console.Error.WriteLine($"[PlaywrightMultiTest] {msg}");
            try { File.AppendAllText(StatusFile, line + "\n"); } catch { }
        }

        private async Task Init()
        {
            try { File.WriteAllText(StatusFile, ""); } catch { } // clear
            LogStatus("Init() started");

            string[] args = Environment.GetCommandLineArgs();
            // Support both --filter=VALUE and --filter VALUE formats
            var filter = args.LastOrDefault(o => o.StartsWith("--filter="))?.Substring(9);
            if (filter == null)
            {
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "--filter")
                    {
                        filter = args[i + 1];
                        break;
                    }
                }
            }


            LogStatus("Discovering projects...");
            var projects = ProjectDiscovery.GetWorkspaceRoot();
            LogStatus($"Found {projects.Count()} projects");
            // add tests to _tests list based on the projects found. You can use the ProjectDetails to determine what kind of project it is and how to get the tests from it. For example, if it's a Blazor WASM project, you might want to start a Playwright instance and navigate to the app to get the tests. If it's a console app, you might want to run the exe with a specific argument to get the tests.
            foreach (var project in projects)
            {
                if (project.AppProjectType == ProjectType.BlazorWasm)
                {
                    var testableProject = new TestableBlazorWasm
                    {
                        ProjectDetails = project,
                    };
                    TestableProjects.Add(testableProject);

                    var buildTest = new ProjectTest(testableProject, $"Build {project.Name}");
                    testableProject.Tests.Add(buildTest);

                    var indexPath = Path.Combine(testableProject.ProjectDetails.WwwRoot, "index.html");

                    // build a publish version of the app for testing
                    LogStatus($"Publishing {project.Name}...");
                    var pubResult = await RunDotnetAsync($"publish \"{project.CsprojPath}\" -c Release", project.Directory).ConfigureAwait(false);
                    LogStatus($"Publish {project.Name}: exit={pubResult}");
                    if (pubResult != 0 || !File.Exists(indexPath))
                    {
                        // build failed
                        buildTest.SetError();
                        continue;
                    }

                    try
                    {
                        LogStatus("Installing Playwright browsers...");
                        var exitCode = Microsoft.Playwright.Program.Main(new[] { "install" });

                        if (exitCode != 0)
                        {
                            throw new Exception($"Playwright browser installation failed with exit code {exitCode}");
                        }

                        // start a static file server to serve the published output
                        // Fixed port so IndexedDB persists across runs (same origin = same IDB)
                        var _port = 5570;
                        var baseUrl = $"https://localhost:{_port}/";
                        testableProject.Server = new StaticFileServer(testableProject.ProjectDetails.WwwRoot, baseUrl);
                        // start https server to serve the Blazor WASM app
                        testableProject.Server.Start();

                        // create a playwright browser, navigate to the app, and enumerate the tests
                        LogStatus("Creating Playwright instance...");
                        testableProject.Playwright = await Playwright.CreateAsync().ConfigureAwait(false);
                        // launch browser
                        // Use persistent context so IndexedDB, localStorage, and
                        // File System Access permissions survive across test runs.
                        // This enables ShaderDebugService's debug folder persistence.
                        var userDataDir = Path.Combine(Path.GetTempPath(), "SpawnDev.RTC.PlaywrightProfile");
                        Directory.CreateDirectory(userDataDir);
                        LogStatus($"Launching Chromium (persistent profile: {userDataDir})...");
                        testableProject.BrowserContext = await testableProject.Playwright.Chromium.LaunchPersistentContextAsync(
                            userDataDir,
                            new BrowserTypeLaunchPersistentContextOptions
                            {
                                Headless = false,
                                Args = new[]
                                {
                                    "--enable-unsafe-webgpu",
                                    "--enable-features=Vulkan,WebGPUService,SkiaGraphite,FileSystemAccessPersistentPermission",
                                    "--ignore-gpu-blocklist",
                                    "--no-sandbox",
                                    // Auto-grant file system write permission (no prompt)
                                    "--disable-features=FileSystemAccessPermissionPrompt",
                                    "--allow-file-access-from-files",
                                    // WebRTC: fake media devices for testing (no real camera/mic needed)
                                    "--use-fake-device-for-media-stream",
                                    "--use-fake-ui-for-media-stream"
                                }
                            }).ConfigureAwait(false);
                        testableProject.Browser = testableProject.BrowserContext.Browser;
                        // Grant all available permissions to avoid prompts
                        await testableProject.BrowserContext.GrantPermissionsAsync(
                            new[] { "clipboard-read", "clipboard-write", "camera", "microphone" }).ConfigureAwait(false);
                        // new page
                        testableProject.Page = await testableProject.BrowserContext.NewPageAsync().ConfigureAwait(false);

                        // Temporary: capture browser console output containing WGSL dumps to a log file
                        var wgslDumpDir = Path.Combine(project.Directory, "..", "PlaywrightMultiTest", "WGSLDumps");
                        Directory.CreateDirectory(wgslDumpDir);
                        var consoleLogPath = Path.Combine(wgslDumpDir, "browser_console.log");
                        File.WriteAllText(consoleLogPath, ""); // clear previous log
                        var wasmDumpChunks = new System.Collections.Generic.List<string>();
                        testableProject.Page.Console += (_, msg) =>
                        {
                            var text = msg.Text;
                            // Capture Wasm binary dumps: collect base64 chunks and write to disk
                            if (text.StartsWith("[Wasm_DUMP]"))
                            {
                                wasmDumpChunks.Add(text.Substring("[Wasm_DUMP]".Length));
                            }
                            else if (text.StartsWith("[Wasm_DUMP_END]") && wasmDumpChunks.Count > 0)
                            {
                                try
                                {
                                    var b64 = string.Join("", wasmDumpChunks);
                                    var bytes = Convert.FromBase64String(b64);
                                    var wasmPath = Path.Combine(wgslDumpDir, $"wasm_dump_{DateTime.Now:HHmmss}.wasm");
                                    File.WriteAllBytes(wasmPath, bytes);
                                    LogStatus($"Wasm binary dumped: {wasmPath} ({bytes.Length} bytes)");
                                }
                                catch (Exception ex) { LogStatus($"Wasm dump failed: {ex.Message}"); }
                                wasmDumpChunks.Clear();
                            }
                            else if (text.StartsWith("[Wasm_DUMP_START]"))
                            {
                                wasmDumpChunks.Clear();
                            }
                            // Only log messages related to WGSL dumps, Wasm worker traces, or errors
                            if (text.Contains("WGSL") || text.Contains("@compute") || text.Contains("@workgroup_size") || text.Contains("WGSL_DUMP") || text.Contains("GLSL_DUMP") || text.Contains("[WasmWorker]") || text.Contains("[Wasm") || text.Contains("CONV2D_TRACE") || text.Contains("TEX_UNIT") || text.Contains("PREPROCESS_TRACE") || text.Contains("LAYER_TRACE") || text.Contains("LOGITS_TRACE") || text.Contains("CPU_LOGITS") || text.Contains("DISP_TRACE") || text.Contains("TF_OFFSET") || msg.Type == "error")
                            {
                                try
                                {
                                    File.AppendAllText(consoleLogPath, $"[{msg.Type}] {text}\n---END_MSG---\n");
                                }
                                catch { }
                            }
                        };

                        // go to the app's unit tests page.
                        var testPageUrl = new Uri(new Uri(baseUrl), testableProject.TestPage).ToString();
                        LogStatus($"Navigating to {testPageUrl}...");
                        await testableProject.Page.GotoAsync(testPageUrl).ConfigureAwait(false);
                        LogStatus("Page loaded, waiting for test table...");

                        // wait for tests to load
                        await testableProject.Page.WaitForSelectorAsync("table.unit-test-ready", new() { Timeout = 30000 }).ConfigureAwait(false);
                        LogStatus("Test table ready");

                        // get the table
                        var table = testableProject.Page.Locator("table.unit-test-view");

                        // get table body
                        var tbody = table.Locator("tbody");

                        // get all rows in the target table body
                        var rows = tbody.Locator("tr");

                        // iterate the rows
                        int rowCount = await rows.CountAsync().ConfigureAwait(false);

                        // wait for the tests to load. This assumes that your Blazor WASM app will render an element with the id "test-list" that contains the list of tests. You would need to implement this in your Blazor WASM app to return the tests you want to run.
                        // get a list of tests

                        for (int i = 0; i < rowCount; i++)
                        {
                            // get the specific row by index
                            var currentRow = rows.Nth(i);

                            // get test type name
                            var typeName = await currentRow.Locator(".test-type-name").TextContentAsync().ConfigureAwait(false);

                            // get test method name
                            var methodName = await currentRow.Locator(".test-method-name").TextContentAsync().ConfigureAwait(false);

                            var rowTest = new ProjectTest(testableProject, typeName!, methodName!, testPageUrl);

                            if (filter != null)
                            {
                                if (rowTest.Name != filter && rowTest.TestTypeName != filter && rowTest.TestMethodName != filter)
                                {
                                    continue;
                                }
                            }

                            testableProject.Tests.Add(rowTest);
                        }
                        LogStatus($"Browser tests enumerated: {testableProject.Tests.Count} tests");

                    }
                    catch (Exception ex)
                    {
                        LogStatus($"Error initializing {project.Name}: {ex.Message}");
                    }
                }
                else if (project.AppProjectType == ProjectType.Exe)
                {
                    // enumerate tests by calling the console app. by default it will return a list of the tests in the exe

                    var testableProject = new TestableConsole
                    {
                        ProjectDetails = project,
                    };
                    TestableProjects.Add(testableProject);

                    var buildTest = new ProjectTest(testableProject, $"Build {project.Name}");
                    testableProject.Tests.Add(buildTest);

                    // build a publish version of the app for testing
                    LogStatus($"Publishing {project.Name}...");
                    var pubResult = await RunDotnetAsync($"publish \"{project.CsprojPath}\" -c Release", project.Directory).ConfigureAwait(false);
                    LogStatus($"Publish {project.Name}: exit={pubResult}");
                    var publishedBinary = project.ExistingPublishBinary;
                    if (pubResult != 0 || string.IsNullOrEmpty(publishedBinary))
                    {
                        // build failed
                        buildTest.SetError();
                        continue;
                    }

                    // get list of tests by running the exe with a specific argument
                    LogStatus($"Enumerating tests from {Path.GetFileName(publishedBinary)}...");
                    var result = await ProcessRunner.Run(publishedBinary).ConfigureAwait(false);
                    LogStatus($"Enumeration done: exit={result.ExitCode}, lines={result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length}");
                    var testList = result.Text.Split(new[] { '\n', '\r' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    foreach (var test in testList)
                    {
                        // get test type name
                        var typeName = test.Split(".")[0];

                        // get test method name
                        var methodName = test.Split(".")[1];

                        var rowTest = new ProjectTest(testableProject, typeName!, methodName!);
                        if (filter != null)
                        {
                            if (rowTest.Name != filter && rowTest.TestTypeName != filter && rowTest.TestMethodName != filter)
                            {
                                continue;
                            }
                        }
                        testableProject.Tests.Add(rowTest);

                        rowTest.TestFunc = async (page) =>
                        {
                            var result = await ProcessRunner.Run(publishedBinary, rowTest.Name, timeout: 120_000).ConfigureAwait(false);
                            var resultLines = result.Text.Split(new[] { '\n', '\r' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                            var testResltTest = resultLines.LastOrDefault(o => o.StartsWith("TEST: "))?.Substring(6);
                            var unitTest = testResltTest != null ? JsonSerializer.Deserialize<UnitTest>(testResltTest) : null;
                            if (unitTest == null)
                            {
                                throw new Exception("Test run failed");
                            }
                            var stateMessage = unitTest.ResultText;
                            rowTest.Result = unitTest.Result;

                            if (rowTest.Result == TestResult.Unsupported)
                            {
                                if (string.IsNullOrWhiteSpace(stateMessage))
                                {
                                    stateMessage = "Skipped";
                                }
                            }
                            else if (rowTest.Result == TestResult.Error)
                            {
                                // Use the actual error details from the test runner, not just
                                // the result enum name ("Error"). unitTest.Error contains the
                                // real exception message and stack trace.
                                var errorDetail = !string.IsNullOrWhiteSpace(unitTest.Error)
                                    ? unitTest.Error
                                    : stateMessage;
                                if (string.IsNullOrWhiteSpace(errorDetail))
                                {
                                    errorDetail = "Failed";
                                }
                                rowTest.ResultMessage = errorDetail;
                                throw new Exception(errorDetail);
                            }
                            else
                            {
                                if (string.IsNullOrWhiteSpace(stateMessage))
                                {
                                    stateMessage = "Success";
                                }
                            }

                            rowTest.ResultMessage = stateMessage;
                            rowTest.Result = unitTest.Result;
                            var nmtt = true;
                        };
                    }

                    var nmt11 = true;
                }
            }
            LogStatus($"Init() complete. Total projects={TestableProjects.Count}, " +
                $"total tests={TestableProjects.Sum(p => p.Tests.Count)}");
            var nmt = true;
        }
        IEnumerable<TestCaseData>? _TestCases;
        public IEnumerable<TestCaseData> TestCases => _TestCases ??= GetPlaywrightTasks();

        /// <summary>
        /// Returns all the tests that are found. This is called before StartUp, so you should not rely on any services or infrastructure being available when this is called. You can return any tests you want to run here, and they will be run by the test runner.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<TestCaseData> GetPlaywrightTasks()
        {
            Debug.WriteLine("GetPlaywrightTasks()");
            foreach (var testableProject in TestableProjects)
            {
                foreach (var test in testableProject.Tests)
                {
                    var testCaseData = new TestCaseData(test).SetName(test.Name).SetCategory(test.TestTypeName ?? test.Name);
                    yield return testCaseData;
                }
            }
            // Cross-platform integration test: desktop peer + browser peer via embedded signal server
            foreach (var testableProject in TestableProjects)
            {
                if (testableProject is TestableBlazorWasm blazor)
                {
                    yield return new TestCaseData(new ProjectTest(blazor, "CrossPlatform", "Desktop_Browser_DataChannel")
                    {
                        TestFunc = async (page) => await CrossPlatformDataChannelTest(page, blazor),
                    }).SetName("CrossPlatform.Desktop_Browser_DataChannel").SetCategory("CrossPlatform");
                    break;
                }
            }
            // Tracker client tests (desktop peers, no 30s UnitTestRunner limit)
            foreach (var testableProject in TestableProjects)
            {
                if (testableProject is TestableBlazorWasm blazor2)
                {
                    yield return new TestCaseData(new ProjectTest(blazor2, "Tracker", "Embedded_TwoPeers")
                    {
                        TestFunc = async (_) => await TrackerEmbeddedTest(blazor2),
                    }).SetName("Tracker.Embedded_TwoPeers").SetCategory("Tracker");

                    yield return new TestCaseData(new ProjectTest(blazor2, "Tracker", "Live_OpenWebTorrent")
                    {
                        TestFunc = async (_) => await TrackerLiveTest(),
                    }).SetName("Tracker.Live_OpenWebTorrent").SetCategory("Tracker");

                    yield return new TestCaseData(new ProjectTest(blazor2, "Tracker", "CrossPlatform_BrowserDesktop")
                    {
                        TestFunc = async (page) => await TrackerCrossPlatformTest(page, blazor2),
                    }).SetName("Tracker.CrossPlatform_BrowserDesktop").SetCategory("Tracker");

                    break;
                }
            }
        }

        /// <summary>
        /// Cross-platform test: a desktop SipSorcery peer and a browser peer
        /// both connect to SignalServer, exchange SDP, and send data channel messages.
        /// </summary>
        private static async Task CrossPlatformDataChannelTest(IPage browserPage, TestableBlazorWasm blazorProj)
        {
            // Signal server is embedded in the same StaticFileServer that serves the Blazor WASM app
            var serverUrl = blazorProj.Server!.Url.TrimEnd('/');
            var roomId = "crossplatform-test-" + Guid.NewGuid().ToString("N")[..6];
            var signalUrl = serverUrl.Replace("https://", "wss://").Replace("http://", "ws://") + $"/signal/{roomId}";
            LogStatus($"[CrossPlatform] Signal room: {signalUrl}");

            // Start desktop peer in background
            var desktopResult = new TaskCompletionSource<string>();
            var desktopTask = Task.Run(async () =>
            {
                try
                {
                    var result = await RunDesktopSignalPeer(signalUrl);
                    desktopResult.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    desktopResult.TrySetException(ex);
                }
            });

            // Give desktop peer a moment to connect to signal server first
            await Task.Delay(1000);

            // Run browser peer via the test page - trigger the Signal_DataChannel_CrossPlatform test
            // But since that test uses a hardcoded room, let's use JS to create a simple peer instead
            var browserResult = await browserPage.EvaluateAsync<string>($@"
                async () => {{
                    try {{
                        const ws = new WebSocket('{signalUrl}');
                        await new Promise((resolve, reject) => {{
                            ws.onopen = resolve;
                            ws.onerror = () => reject('WebSocket failed');
                            setTimeout(() => reject('WebSocket timeout'), 5000);
                        }});

                        let myId = '';
                        let remoteId = '';
                        const pc = new RTCPeerConnection({{ iceServers: [{{ urls: 'stun:stun.l.google.com:19302' }}] }});
                        const dc = pc.createDataChannel('cross-platform');

                        const dcOpen = new Promise(resolve => dc.onopen = resolve);
                        const msgReceived = new Promise(resolve => dc.onmessage = e => resolve(e.data));

                        pc.onicecandidate = e => {{
                            if (e.candidate) {{
                                ws.send(JSON.stringify({{ type: 'ice-candidate', targetId: remoteId, candidate: e.candidate.candidate, sdpMid: e.candidate.sdpMid, sdpMLineIndex: e.candidate.sdpMLineIndex }}));
                            }}
                        }};

                        ws.onmessage = async e => {{
                            const msg = JSON.parse(e.data);
                            if (msg.type === 'welcome') {{
                                myId = msg.peerId;
                                if (msg.peers.length > 0) {{
                                    remoteId = msg.peers[0];
                                    const offer = await pc.createOffer();
                                    await pc.setLocalDescription(offer);
                                    ws.send(JSON.stringify({{ type: 'offer', targetId: remoteId, sdp: offer.sdp }}));
                                }}
                            }} else if (msg.type === 'peer-joined') {{
                                remoteId = msg.peerId;
                            }} else if (msg.type === 'offer') {{
                                remoteId = msg.fromId;
                                await pc.setRemoteDescription({{ type: 'offer', sdp: msg.sdp }});
                                const answer = await pc.createAnswer();
                                await pc.setLocalDescription(answer);
                                ws.send(JSON.stringify({{ type: 'answer', targetId: remoteId, sdp: answer.sdp }}));
                            }} else if (msg.type === 'answer') {{
                                await pc.setRemoteDescription({{ type: 'answer', sdp: msg.sdp }});
                            }} else if (msg.type === 'ice-candidate') {{
                                await pc.addIceCandidate({{ candidate: msg.candidate, sdpMid: msg.sdpMid, sdpMLineIndex: msg.sdpMLineIndex }});
                            }}
                        }};

                        await Promise.race([dcOpen, new Promise((_, reject) => setTimeout(() => reject('DC open timeout'), 30000))]);
                        dc.send('Hello from browser!');
                        const received = await Promise.race([msgReceived, new Promise((_, reject) => setTimeout(() => reject('Message timeout'), 10000))]);
                        ws.close();
                        pc.close();
                        return 'OK:' + received;
                    }} catch (err) {{
                        return 'ERROR:' + (err.message || err);
                    }}
                }}
            ");

            LogStatus($"[CrossPlatform] Browser result: {{browserResult}}");

            // Wait for desktop result
            var desktopMsg = await Task.WhenAny(desktopResult.Task, Task.Delay(35000));
            string desktopResultStr;
            if (desktopMsg == desktopResult.Task)
                desktopResultStr = await desktopResult.Task;
            else
                desktopResultStr = "TIMEOUT";

            LogStatus($"[CrossPlatform] Desktop result: {{desktopResultStr}}");

            if (!browserResult.StartsWith("OK:"))
                throw new Exception($"Browser peer failed: {{browserResult}}");
            if (!desktopResultStr.StartsWith("OK:"))
                throw new Exception($"Desktop peer failed: {{desktopResultStr}}");

            LogStatus("[CrossPlatform] SUCCESS - Desktop and browser exchanged data channel messages!");
        }

        /// <summary>
        /// Runs a desktop SipSorcery peer that connects to the signal server,
        /// exchanges SDP with the browser peer, and sends/receives a data channel message.
        /// </summary>
        private static async Task<string> RunDesktopSignalPeer(string signalUrl)
        {
            using var pc = SpawnDev.RTC.RTCPeerConnectionFactory.Create(new SpawnDev.RTC.RTCPeerConnectionConfig
            {
                IceServers = new[] { new SpawnDev.RTC.RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            });

            using var ws = new System.Net.WebSockets.ClientWebSocket();
            ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            await ws.ConnectAsync(new Uri(signalUrl), CancellationToken.None);

            string myPeerId = "", remotePeerId = "";
            SpawnDev.RTC.IRTCDataChannel? dc = null;
            var channelOpened = new TaskCompletionSource<bool>();
            var messageReceived = new TaskCompletionSource<string>();

            pc.OnDataChannel += channel =>
            {
                dc = channel;
                dc.OnOpen += () => channelOpened.TrySetResult(true);
                dc.OnStringMessage += msg => messageReceived.TrySetResult(msg);
            };

            pc.OnIceCandidate += async candidate =>
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "ice-candidate", targetId = remotePeerId,
                    candidate = candidate.Candidate, sdpMid = candidate.SdpMid, sdpMLineIndex = candidate.SdpMLineIndex,
                });
                await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(json),
                    System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
            };

            // Signal message processing
            var buffer = new byte[64 * 1024];
            var signalDone = new TaskCompletionSource<bool>();
            _ = Task.Run(async () =>
            {
                while (ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                    var json = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var msg = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
                    var msgType = msg.GetProperty("type").GetString();

                    if (msgType == "welcome")
                    {
                        myPeerId = msg.GetProperty("peerId").GetString()!;
                        var peers = msg.GetProperty("peers");
                        if (peers.GetArrayLength() > 0)
                        {
                            remotePeerId = peers[0].GetString()!;
                            dc = pc.CreateDataChannel("cross-platform");
                            dc.OnOpen += () => channelOpened.TrySetResult(true);
                            dc.OnStringMessage += m => messageReceived.TrySetResult(m);
                            var offer = await pc.CreateOffer();
                            await pc.SetLocalDescription(offer);
                            var offerJson = System.Text.Json.JsonSerializer.Serialize(new { type = "offer", targetId = remotePeerId, sdp = offer.Sdp });
                            await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(offerJson),
                                System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                    }
                    else if (msgType == "peer-joined")
                    {
                        remotePeerId = msg.GetProperty("peerId").GetString()!;
                    }
                    else if (msgType == "offer")
                    {
                        remotePeerId = msg.GetProperty("fromId").GetString()!;
                        await pc.SetRemoteDescription(new SpawnDev.RTC.RTCSessionDescriptionInit { Type = "offer", Sdp = msg.GetProperty("sdp").GetString()! });
                        var answer = await pc.CreateAnswer();
                        await pc.SetLocalDescription(answer);
                        var answerJson = System.Text.Json.JsonSerializer.Serialize(new { type = "answer", targetId = remotePeerId, sdp = answer.Sdp });
                        await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(answerJson),
                            System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    else if (msgType == "answer")
                    {
                        await pc.SetRemoteDescription(new SpawnDev.RTC.RTCSessionDescriptionInit { Type = "answer", Sdp = msg.GetProperty("sdp").GetString()! });
                    }
                    else if (msgType == "ice-candidate")
                    {
                        await pc.AddIceCandidate(new SpawnDev.RTC.RTCIceCandidateInit
                        {
                            Candidate = msg.GetProperty("candidate").GetString()!,
                            SdpMid = msg.TryGetProperty("sdpMid", out var mid) ? mid.GetString() : null,
                            SdpMLineIndex = msg.TryGetProperty("sdpMLineIndex", out var mli) ? mli.GetInt32() : null,
                        });
                    }
                }
            });

            // Wait for channel to open
            var openResult = await Task.WhenAny(channelOpened.Task, Task.Delay(30000));
            if (openResult != channelOpened.Task)
                return "ERROR:Channel did not open";

            dc!.Send("Hello from desktop!");

            var msgResult = await Task.WhenAny(messageReceived.Task, Task.Delay(10000));
            if (msgResult != messageReceived.Task)
                return "ERROR:No response received";

            return "OK:" + await messageReceived.Task;
        }

        /// <summary>
        /// This is called after tests have been enumerated bu before they are run. You can use this to start up any services or infrastructure needed for the tests.
        /// </summary>
        /// <returns></returns>
        /// <summary>
        /// Two desktop peers connect via the embedded WebTorrent tracker and exchange data channel messages.
        /// </summary>
        private static async Task TrackerEmbeddedTest(TestableBlazorWasm blazorProj)
        {
            var trackerUrl = blazorProj.Server!.Url.TrimEnd('/').Replace("https://", "wss://") + "/announce";
            var room = "embedded-test-" + Guid.NewGuid().ToString("N")[..6];
            LogStatus($"[Tracker Embedded] URL: {trackerUrl}, Room: {room}");

            var config = new SpawnDev.RTC.RTCPeerConnectionConfig
            {
                IceServers = new[] { new SpawnDev.RTC.RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            var msgFromB = new TaskCompletionSource<string>();
            using var tA = new SpawnDev.RTC.RTCTrackerClient(trackerUrl, room, config);
            tA.OnDataChannel += (ch, id) =>
            {
                LogStatus($"[Tracker Embedded] A got dc '{ch.Label}' state={ch.ReadyState}");
                ch.OnStringMessage += m => { LogStatus($"[Tracker Embedded] A got: {m}"); msgFromB.TrySetResult(m); };
                ch.OnOpen += () => { LogStatus("[Tracker Embedded] A dc open"); };
                // Send after a short delay to ensure both sides have wired handlers
                Task.Run(async () => { await Task.Delay(500); ch.Send("from A"); LogStatus("[Tracker Embedded] A sent"); });
            };
            tA.OnConnected += () => LogStatus("[Tracker Embedded] A connected");
            tA.OnPeerConnection += (_, id) => LogStatus($"[Tracker Embedded] A peer: {id[..8]}");

            var msgFromA = new TaskCompletionSource<string>();
            using var tB = new SpawnDev.RTC.RTCTrackerClient(trackerUrl, room, config);
            tB.OnDataChannel += (ch, id) =>
            {
                LogStatus($"[Tracker Embedded] B got dc '{ch.Label}' state={ch.ReadyState}");
                ch.OnStringMessage += m => { LogStatus($"[Tracker Embedded] B got: {m}"); msgFromA.TrySetResult(m); };
                ch.OnOpen += () => { LogStatus("[Tracker Embedded] B dc open"); };
                Task.Run(async () => { await Task.Delay(500); ch.Send("from B"); LogStatus("[Tracker Embedded] B sent"); });
            };
            tB.OnConnected += () => LogStatus("[Tracker Embedded] B connected");
            tB.OnPeerConnection += (_, id) => LogStatus($"[Tracker Embedded] B peer: {id[..8]}");

            await tA.JoinAsync();
            await Task.Delay(1000);
            await tB.JoinAsync();

            var rA = await Task.WhenAny(msgFromB.Task, Task.Delay(25000));
            var rB = await Task.WhenAny(msgFromA.Task, Task.Delay(25000));

            if (rA != msgFromB.Task) throw new Exception("A did not receive from B");
            if (rB != msgFromA.Task) throw new Exception("B did not receive from A");
            LogStatus($"[Tracker Embedded] A got: {await msgFromB.Task}, B got: {await msgFromA.Task}");
        }

        /// <summary>
        /// Two desktop peers connect via the live openwebtorrent tracker.
        /// </summary>
        private static async Task TrackerLiveTest()
        {
            var room = "live-test-" + Guid.NewGuid().ToString("N")[..8];
            LogStatus($"[Tracker Live] Room: {room}");

            var config = new SpawnDev.RTC.RTCPeerConnectionConfig
            {
                IceServers = new[] { new SpawnDev.RTC.RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            var msgFromB = new TaskCompletionSource<string>();
            using var tA = new SpawnDev.RTC.RTCTrackerClient("wss://tracker.openwebtorrent.com", room, config);
            tA.OnDataChannel += (ch, _) =>
            {
                LogStatus($"[Tracker Live] A got dc '{ch.Label}' state={ch.ReadyState}");
                ch.OnStringMessage += m => { LogStatus($"[Tracker Live] A got: {m}"); msgFromB.TrySetResult(m); };
                ch.OnOpen += () => LogStatus("[Tracker Live] A dc open");
                Task.Run(async () => { await Task.Delay(500); ch.Send("live A"); LogStatus("[Tracker Live] A sent"); });
            };
            tA.OnConnected += () => LogStatus("[Tracker Live] A connected to openwebtorrent");
            tA.OnPeerConnection += (_, id) => LogStatus($"[Tracker Live] A peer: {id[..8]}");

            var msgFromA = new TaskCompletionSource<string>();
            using var tB = new SpawnDev.RTC.RTCTrackerClient("wss://tracker.openwebtorrent.com", room, config);
            tB.OnDataChannel += (ch, _) =>
            {
                LogStatus($"[Tracker Live] B got dc '{ch.Label}' state={ch.ReadyState}");
                ch.OnStringMessage += m => { LogStatus($"[Tracker Live] B got: {m}"); msgFromA.TrySetResult(m); };
                ch.OnOpen += () => LogStatus("[Tracker Live] B dc open");
                Task.Run(async () => { await Task.Delay(500); ch.Send("live B"); LogStatus("[Tracker Live] B sent"); });
            };
            tB.OnConnected += () => LogStatus("[Tracker Live] B connected to openwebtorrent");
            tB.OnPeerConnection += (_, id) => LogStatus($"[Tracker Live] B peer: {id[..8]}");

            await tA.JoinAsync();
            await Task.Delay(2000);
            await tB.JoinAsync();

            var rA = await Task.WhenAny(msgFromB.Task, Task.Delay(45000));
            var rB = await Task.WhenAny(msgFromA.Task, Task.Delay(45000));

            if (rA != msgFromB.Task) throw new Exception("A did not receive from B via live tracker");
            if (rB != msgFromA.Task) throw new Exception("B did not receive from A via live tracker");
            LogStatus($"[Tracker Live] SUCCESS - A got: {await msgFromB.Task}, B got: {await msgFromA.Task}");
        }

        /// <summary>
        /// Cross-platform tracker test: desktop peer + browser peer connect
        /// via embedded WebTorrent tracker and exchange data channel messages.
        /// </summary>
        private static async Task TrackerCrossPlatformTest(IPage browserPage, TestableBlazorWasm blazorProj)
        {
            var trackerUrl = blazorProj.Server!.Url.TrimEnd('/').Replace("https://", "wss://") + "/announce";
            var room = "xplat-tracker-" + Guid.NewGuid().ToString("N")[..6];
            LogStatus($"[Tracker XPlat] URL: {trackerUrl}, Room: {room}");

            var config = new SpawnDev.RTC.RTCPeerConnectionConfig
            {
                IceServers = new[] { new SpawnDev.RTC.RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            // Desktop peer in background
            var desktopMsg = new TaskCompletionSource<string>();
            using var desktopTracker = new SpawnDev.RTC.RTCTrackerClient(trackerUrl, room, config);
            desktopTracker.OnDataChannel += (ch, _) =>
            {
                ch.OnStringMessage += m => { LogStatus($"[Tracker XPlat] Desktop got: {m}"); desktopMsg.TrySetResult(m); };
                Task.Run(async () => { await Task.Delay(500); ch.Send("from desktop"); LogStatus("[Tracker XPlat] Desktop sent"); });
            };
            desktopTracker.OnConnected += () => LogStatus("[Tracker XPlat] Desktop connected");
            desktopTracker.OnPeerConnection += (_, id) => LogStatus($"[Tracker XPlat] Desktop peer: {id[..8]}");

            await desktopTracker.JoinAsync();
            await Task.Delay(1000);

            // Browser peer via JS
            var wsUrl = trackerUrl;
            var browserResult = await browserPage.EvaluateAsync<string>($@"
                async () => {{
                    try {{
                        const ws = new WebSocket('{wsUrl}');
                        await new Promise((resolve, reject) => {{
                            ws.onopen = resolve;
                            ws.onerror = () => reject('ws failed');
                            setTimeout(() => reject('ws timeout'), 5000);
                        }});

                        const peerId = '-BR0100-' + Math.random().toString(36).substr(2, 12);
                        const infoHashBytes = new Uint8Array(await crypto.subtle.digest('SHA-1',
                            new TextEncoder().encode('{room}'.trim().toLowerCase())));
                        const infoHash = String.fromCharCode(...infoHashBytes);

                        const pc = new RTCPeerConnection({{ iceServers: [{{ urls: 'stun:stun.l.google.com:19302' }}] }});
                        const dc = pc.createDataChannel('data');

                        const dcOpen = new Promise(r => dc.onopen = r);
                        const msgReceived = new Promise(r => dc.onmessage = e => r(e.data));

                        pc.onicecandidate = () => {{}};

                        const offer = await pc.createOffer();
                        await pc.setLocalDescription(offer);

                        const offerId = String.fromCharCode(...crypto.getRandomValues(new Uint8Array(20)));

                        ws.send(JSON.stringify({{
                            action: 'announce',
                            info_hash: infoHash,
                            peer_id: peerId,
                            uploaded: 0, downloaded: 0, left: 1,
                            event: 'started',
                            numwant: 5,
                            offers: [{{ offer: {{ type: 'offer', sdp: offer.sdp }}, offer_id: offerId }}]
                        }}));

                        // Wait for offer from desktop peer
                        const msg = await new Promise((resolve, reject) => {{
                            ws.onmessage = e => resolve(JSON.parse(e.data));
                            setTimeout(() => reject('no offer'), 15000);
                        }});

                        if (msg.offer) {{
                            await pc.setRemoteDescription({{ type: 'offer', sdp: msg.offer.sdp }});
                            const answer = await pc.createAnswer();
                            await pc.setLocalDescription(answer);
                            ws.send(JSON.stringify({{
                                action: 'announce',
                                info_hash: infoHash,
                                peer_id: peerId,
                                to_peer_id: msg.peer_id,
                                answer: {{ type: 'answer', sdp: answer.sdp }},
                                offer_id: msg.offer_id
                            }}));
                        }} else if (msg.answer) {{
                            await pc.setRemoteDescription({{ type: 'answer', sdp: msg.answer.sdp }});
                        }}

                        await Promise.race([dcOpen, new Promise((_, r) => setTimeout(() => r('dc timeout'), 15000))]);
                        dc.send('from browser');
                        const received = await Promise.race([msgReceived, new Promise((_, r) => setTimeout(() => r('msg timeout'), 10000))]);
                        ws.close();
                        pc.close();
                        return 'OK:' + received;
                    }} catch (err) {{
                        return 'ERROR:' + (err.message || err);
                    }}
                }}
            ");

            LogStatus($"[Tracker XPlat] Browser result: {browserResult}");

            var dr = await Task.WhenAny(desktopMsg.Task, Task.Delay(20000));
            var desktopResult = dr == desktopMsg.Task ? "OK:" + await desktopMsg.Task : "TIMEOUT";
            LogStatus($"[Tracker XPlat] Desktop result: {desktopResult}");

            if (!browserResult.StartsWith("OK:")) throw new Exception($"Browser: {browserResult}");
            if (!desktopResult.StartsWith("OK:")) throw new Exception($"Desktop: {desktopResult}");

            LogStatus("[Tracker XPlat] SUCCESS - Browser and desktop exchanged messages via WebTorrent tracker!");
        }

        public async Task StartUp()
        {
            Debug.WriteLine("StartUp()");
        }

        /// <summary>
        /// This is called after tests have ran. You can use this to stop any services or infrastructure started in StartUp.
        /// </summary>
        /// <returns></returns>
        public async Task Shutdown()
        {
            Debug.WriteLine("Shutdown()");
            foreach (var testableProject in TestableProjects)
            {
                if (testableProject is TestableBlazorWasm blazorProj)
                {
                    try { if (blazorProj.Page != null) await blazorProj.Page.CloseAsync().ConfigureAwait(false); } catch { }
                    try { if (blazorProj.BrowserContext != null) await blazorProj.BrowserContext.CloseAsync().ConfigureAwait(false); } catch { }
                    try { if (blazorProj.Browser != null) await blazorProj.Browser.CloseAsync().ConfigureAwait(false); } catch { }
                    try { blazorProj.Playwright?.Dispose(); } catch { }
                    try { if (blazorProj.Server != null) await blazorProj.Server.Stop().ConfigureAwait(false); } catch { }
                }
                else if (testableProject is TestableConsole consoleProj)
                {
                    // do any cleanup needed for console projects
                }
            }
        }
    }
}