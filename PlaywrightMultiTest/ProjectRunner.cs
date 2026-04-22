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

        /// <summary>
        /// Matches a filter against a test. Supports exact match on Name, TestTypeName,
        /// or TestMethodName, plus trailing-* prefix match on TestMethodName
        /// (e.g. "RoomKey_*" matches every method whose name starts with "RoomKey_").
        /// </summary>
        private static bool FilterMatches(string filter, ProjectTest test)
        {
            if (test.Name == filter || test.TestTypeName == filter || test.TestMethodName == filter)
                return true;
            if (filter.EndsWith("*"))
            {
                var prefix = filter.Substring(0, filter.Length - 1);
                if (test.TestMethodName?.StartsWith(prefix) == true) return true;
                if (test.TestTypeName?.StartsWith(prefix) == true) return true;
            }
            return false;
        }

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
            // Support both --filter=VALUE and --filter VALUE formats.
            // dotnet test swallows --filter before it reaches us, so also honor
            // PLAYWRIGHT_TEST_FILTER env var as the reliable way to pass a filter
            // through the dotnet test harness.
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
            if (string.IsNullOrEmpty(filter))
            {
                var envFilter = Environment.GetEnvironmentVariable("PLAYWRIGHT_TEST_FILTER");
                if (!string.IsNullOrEmpty(envFilter)) filter = envFilter;
            }
            LogStatus($"Filter: {(filter ?? "(none — run all)")}");


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

                            if (filter != null && !FilterMatches(filter, rowTest))
                            {
                                continue;
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
                        if (filter != null && !FilterMatches(filter, rowTest))
                        {
                            continue;
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
            // Generic-signaling client tests: TrackerSignalingClient + RtcPeerConnectionRoomHandler.
            // Prove SpawnDev.RTC consumers can do serverless WebRTC signaling with zero WebTorrent
            // references. Desktop peers, no 30s UnitTestRunner limit.
            foreach (var testableProject in TestableProjects)
            {
                if (testableProject is TestableBlazorWasm blazor2)
                {
                    yield return new TestCaseData(new ProjectTest(blazor2, "Signaling", "Embedded_TwoPeers")
                    {
                        TestFunc = async (_) => await SignalingEmbeddedTest(blazor2),
                    }).SetName("Signaling.Embedded_TwoPeers").SetCategory("Signaling");

                    yield return new TestCaseData(new ProjectTest(blazor2, "Signaling", "Live_OpenWebTorrent")
                    {
                        TestFunc = async (_) => await SignalingLiveTest(),
                    }).SetName("Signaling.Live_OpenWebTorrent").SetCategory("Signaling");

                    yield return new TestCaseData(new ProjectTest(blazor2, "Signaling", "CrossPlatform_BrowserDesktop")
                    {
                        TestFunc = async (page) => await SignalingCrossPlatformTest(page, blazor2),
                    }).SetName("Signaling.CrossPlatform_BrowserDesktop").SetCategory("Signaling");

                    yield return new TestCaseData(new ProjectTest(blazor2, "Signaling", "RoomIsolation")
                    {
                        TestFunc = async (_) => await SignalingRoomIsolationTest(blazor2),
                    }).SetName("Signaling.RoomIsolation").SetCategory("Signaling");

                    break;
                }
            }
        }

        /// <summary>
        /// Two raw WebSocket clients announce into two different rooms against the embedded
        /// <c>SpawnDev.RTC.Server</c> tracker. Each verifies the tracker's announce response
        /// contains only peers from its own room - peers in room A must never appear in the
        /// peer list delivered to room B's peer, and vice versa. Exercises the library's
        /// public <c>UseRtcSignaling</c> surface directly via the dogfooded embedded server.
        /// </summary>
        private static async Task SignalingRoomIsolationTest(TestableBlazorWasm blazorProj)
        {
            var trackerUrl = blazorProj.Server!.Url.TrimEnd('/').Replace("https://", "wss://") + "/announce";
            var roomA = SpawnDev.RTC.Signaling.RoomKey.FromString("iso-A-" + Guid.NewGuid().ToString("N")[..6]).ToWireString();
            var roomB = SpawnDev.RTC.Signaling.RoomKey.FromString("iso-B-" + Guid.NewGuid().ToString("N")[..6]).ToWireString();
            var peerA = "-RT0100-" + Guid.NewGuid().ToString("N")[..12];
            var peerB = "-RT0100-" + Guid.NewGuid().ToString("N")[..12];
            LogStatus($"[Signaling Isolation] Rooms A={roomA.Length} bytes, B={roomB.Length} bytes, peers A={peerA[..8]}, B={peerB[..8]}");

            using var wsA = new System.Net.WebSockets.ClientWebSocket();
            wsA.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            using var wsB = new System.Net.WebSockets.ClientWebSocket();
            wsB.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await wsA.ConnectAsync(new Uri(trackerUrl), cts.Token);
            await wsB.ConnectAsync(new Uri(trackerUrl), cts.Token);
            LogStatus("[Signaling Isolation] Both WS connected");

            await SendIsolationAnnounce(wsA, roomA, peerA, cts.Token);
            await SendIsolationAnnounce(wsB, roomB, peerB, cts.Token);

            var respA = await ReceiveOneJsonObject(wsA, cts.Token);
            var respB = await ReceiveOneJsonObject(wsB, cts.Token);
            LogStatus($"[Signaling Isolation] A response: {respA}");
            LogStatus($"[Signaling Isolation] B response: {respB}");

            var docA = System.Text.Json.JsonDocument.Parse(respA);
            var docB = System.Text.Json.JsonDocument.Parse(respB);
            var peersA = docA.RootElement.TryGetProperty("peers", out var pA) && pA.ValueKind == System.Text.Json.JsonValueKind.Array
                ? pA.EnumerateArray().Select(e => e.GetProperty("peer_id").GetString()!).ToArray()
                : Array.Empty<string>();
            var peersB = docB.RootElement.TryGetProperty("peers", out var pB) && pB.ValueKind == System.Text.Json.JsonValueKind.Array
                ? pB.EnumerateArray().Select(e => e.GetProperty("peer_id").GetString()!).ToArray()
                : Array.Empty<string>();

            if (peersA.Contains(peerB)) throw new Exception($"Room isolation broken: A saw B ({peerB}) in its peer list");
            if (peersB.Contains(peerA)) throw new Exception($"Room isolation broken: B saw A ({peerA}) in its peer list");

            // Rely on `using var` to tear down the sockets. Explicit CloseAsync here raced
            // with the server's receive loop and tripped WebSocketException on some runs.
            LogStatus("[Signaling Isolation] SUCCESS - rooms are isolated");
        }

        private static async Task SendIsolationAnnounce(System.Net.WebSockets.ClientWebSocket ws, string roomKey, string peerId, CancellationToken ct)
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "announce",
                info_hash = roomKey,
                peer_id = peerId,
                uploaded = 0,
                downloaded = 0,
                left = 1,
                @event = "started",
                numwant = 10,
            }, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
            await ws.SendAsync(bytes, System.Net.WebSockets.WebSocketMessageType.Text, true, ct);
        }

        private static async Task<string> ReceiveOneJsonObject(System.Net.WebSockets.ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[16384];
            using var ms = new MemoryStream();
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    throw new Exception("WS closed before announce response arrived");
                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) break;
            }
            return System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }

        // ============================================================
        // Signaling.* tests - exercise the new TrackerSignalingClient +
        // RtcPeerConnectionRoomHandler surface (zero WebTorrent refs).
        // ============================================================

        /// <summary>
        /// Two desktop peers using <see cref="SpawnDev.RTC.Signaling.TrackerSignalingClient"/>
        /// and <see cref="SpawnDev.RTC.Signaling.RtcPeerConnectionRoomHandler"/> announce to the
        /// same <see cref="SpawnDev.RTC.Signaling.RoomKey"/> on the embedded tracker, exchange
        /// offer/answer, open a data channel, and pass messages both directions.
        /// </summary>
        private static async Task SignalingEmbeddedTest(TestableBlazorWasm blazorProj)
        {
            var trackerUrl = blazorProj.Server!.Url.TrimEnd('/').Replace("https://", "wss://") + "/announce";
            var room = SpawnDev.RTC.Signaling.RoomKey.FromString("signaling-embedded-" + Guid.NewGuid().ToString("N")[..6]);
            LogStatus($"[Signaling Embedded] URL: {trackerUrl}, Room: {room.ToHex()[..8]}...");

            var config = new SpawnDev.RTC.RTCPeerConnectionConfig
            {
                IceServers = new[] { new SpawnDev.RTC.RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            var peerIdA = NewRandomPeerId();
            var peerIdB = NewRandomPeerId();

            var msgFromB = new TaskCompletionSource<string>();
            var handlerA = new SpawnDev.RTC.Signaling.RtcPeerConnectionRoomHandler(config);
            handlerA.OnDataChannel += (ch, id) =>
            {
                LogStatus($"[Signaling Embedded] A got dc '{ch.Label}' state={ch.ReadyState}");
                ch.OnStringMessage += m => { LogStatus($"[Signaling Embedded] A got: {m}"); msgFromB.TrySetResult(m); };
                ch.OnOpen += () => { LogStatus("[Signaling Embedded] A dc open"); };
                Task.Run(async () => { await Task.Delay(500); ch.Send("from A"); LogStatus("[Signaling Embedded] A sent"); });
            };
            handlerA.OnPeerConnection += (_, id) => LogStatus($"[Signaling Embedded] A peer: {id[..8]}");

            var msgFromA = new TaskCompletionSource<string>();
            var handlerB = new SpawnDev.RTC.Signaling.RtcPeerConnectionRoomHandler(config);
            handlerB.OnDataChannel += (ch, id) =>
            {
                LogStatus($"[Signaling Embedded] B got dc '{ch.Label}' state={ch.ReadyState}");
                ch.OnStringMessage += m => { LogStatus($"[Signaling Embedded] B got: {m}"); msgFromA.TrySetResult(m); };
                ch.OnOpen += () => { LogStatus("[Signaling Embedded] B dc open"); };
                Task.Run(async () => { await Task.Delay(500); ch.Send("from B"); LogStatus("[Signaling Embedded] B sent"); });
            };
            handlerB.OnPeerConnection += (_, id) => LogStatus($"[Signaling Embedded] B peer: {id[..8]}");

            await using var clientA = new SpawnDev.RTC.Signaling.TrackerSignalingClient(trackerUrl, peerIdA);
            await using var clientB = new SpawnDev.RTC.Signaling.TrackerSignalingClient(trackerUrl, peerIdB);

            clientA.OnConnected += () => LogStatus("[Signaling Embedded] A connected");
            clientB.OnConnected += () => LogStatus("[Signaling Embedded] B connected");
            clientA.OnWarning += w => LogStatus($"[Signaling Embedded] A warning: {w}");
            clientB.OnWarning += w => LogStatus($"[Signaling Embedded] B warning: {w}");

            clientA.Subscribe(room, handlerA);
            clientB.Subscribe(room, handlerB);

            await clientA.AnnounceAsync(room, new SpawnDev.RTC.Signaling.AnnounceOptions { Event = "started", NumWant = 5 });
            await Task.Delay(1000);
            await clientB.AnnounceAsync(room, new SpawnDev.RTC.Signaling.AnnounceOptions { Event = "started", NumWant = 5 });

            var rA = await Task.WhenAny(msgFromB.Task, Task.Delay(25000));
            var rB = await Task.WhenAny(msgFromA.Task, Task.Delay(25000));

            handlerA.Dispose();
            handlerB.Dispose();

            if (rA != msgFromB.Task) throw new Exception("A did not receive from B via new signaling client");
            if (rB != msgFromA.Task) throw new Exception("B did not receive from A via new signaling client");
            LogStatus($"[Signaling Embedded] SUCCESS - A got: {await msgFromB.Task}, B got: {await msgFromA.Task}");
        }

        /// <summary>
        /// Two desktop peers using <see cref="SpawnDev.RTC.Signaling.TrackerSignalingClient"/>
        /// meet via the live <c>wss://tracker.openwebtorrent.com</c> tracker, exchange offer/answer,
        /// and pass messages both directions. Proves the new client is wire-compatible with the
        /// public tracker fleet.
        /// </summary>
        private static async Task SignalingLiveTest()
        {
            const string trackerUrl = "wss://tracker.openwebtorrent.com";
            var room = SpawnDev.RTC.Signaling.RoomKey.FromString("signaling-live-" + Guid.NewGuid().ToString("N")[..8]);
            LogStatus($"[Signaling Live] Tracker: {trackerUrl}, Room: {room.ToHex()[..8]}...");

            var config = new SpawnDev.RTC.RTCPeerConnectionConfig
            {
                IceServers = new[] { new SpawnDev.RTC.RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            var peerIdA = NewRandomPeerId();
            var peerIdB = NewRandomPeerId();

            var msgFromB = new TaskCompletionSource<string>();
            var handlerA = new SpawnDev.RTC.Signaling.RtcPeerConnectionRoomHandler(config);
            handlerA.OnDataChannel += (ch, _) =>
            {
                ch.OnStringMessage += m => { LogStatus($"[Signaling Live] A got: {m}"); msgFromB.TrySetResult(m); };
                ch.OnOpen += () => LogStatus("[Signaling Live] A dc open");
                Task.Run(async () => { await Task.Delay(500); ch.Send("live A"); LogStatus("[Signaling Live] A sent"); });
            };
            handlerA.OnPeerConnection += (_, id) => LogStatus($"[Signaling Live] A peer: {id[..8]}");

            var msgFromA = new TaskCompletionSource<string>();
            var handlerB = new SpawnDev.RTC.Signaling.RtcPeerConnectionRoomHandler(config);
            handlerB.OnDataChannel += (ch, _) =>
            {
                ch.OnStringMessage += m => { LogStatus($"[Signaling Live] B got: {m}"); msgFromA.TrySetResult(m); };
                ch.OnOpen += () => LogStatus("[Signaling Live] B dc open");
                Task.Run(async () => { await Task.Delay(500); ch.Send("live B"); LogStatus("[Signaling Live] B sent"); });
            };
            handlerB.OnPeerConnection += (_, id) => LogStatus($"[Signaling Live] B peer: {id[..8]}");

            await using var clientA = new SpawnDev.RTC.Signaling.TrackerSignalingClient(trackerUrl, peerIdA);
            await using var clientB = new SpawnDev.RTC.Signaling.TrackerSignalingClient(trackerUrl, peerIdB);

            clientA.OnConnected += () => LogStatus("[Signaling Live] A connected");
            clientB.OnConnected += () => LogStatus("[Signaling Live] B connected");

            clientA.Subscribe(room, handlerA);
            clientB.Subscribe(room, handlerB);

            await clientA.AnnounceAsync(room, new SpawnDev.RTC.Signaling.AnnounceOptions { Event = "started", NumWant = 5 });
            await Task.Delay(2000);
            await clientB.AnnounceAsync(room, new SpawnDev.RTC.Signaling.AnnounceOptions { Event = "started", NumWant = 5 });

            var rA = await Task.WhenAny(msgFromB.Task, Task.Delay(45000));
            var rB = await Task.WhenAny(msgFromA.Task, Task.Delay(45000));

            handlerA.Dispose();
            handlerB.Dispose();

            if (rA != msgFromB.Task) throw new Exception("A did not receive from B via live tracker");
            if (rB != msgFromA.Task) throw new Exception("B did not receive from A via live tracker");
            LogStatus($"[Signaling Live] SUCCESS - A got: {await msgFromB.Task}, B got: {await msgFromA.Task}");
        }

        /// <summary>
        /// Cross-platform signaling test: desktop peer uses <see cref="SpawnDev.RTC.Signaling.TrackerSignalingClient"/>;
        /// browser peer speaks the raw WebTorrent tracker wire protocol from JavaScript. Proves the new
        /// signaling client's wire format is byte-compatible with any plain JS WebTorrent peer.
        /// </summary>
        private static async Task SignalingCrossPlatformTest(IPage browserPage, TestableBlazorWasm blazorProj)
        {
            var trackerUrl = blazorProj.Server!.Url.TrimEnd('/').Replace("https://", "wss://") + "/announce";
            var roomName = "xplat-signaling-" + Guid.NewGuid().ToString("N")[..6];
            var room = SpawnDev.RTC.Signaling.RoomKey.FromString(roomName);
            LogStatus($"[Signaling XPlat] URL: {trackerUrl}, Room: {room.ToHex()[..8]}...");

            var config = new SpawnDev.RTC.RTCPeerConnectionConfig
            {
                IceServers = new[] { new SpawnDev.RTC.RTCIceServerConfig { Urls = new[] { "stun:stun.l.google.com:19302" } } }
            };

            // Desktop peer using the new API.
            var desktopMsg = new TaskCompletionSource<string>();
            var handler = new SpawnDev.RTC.Signaling.RtcPeerConnectionRoomHandler(config);
            handler.OnDataChannel += (ch, _) =>
            {
                ch.OnStringMessage += m => { LogStatus($"[Signaling XPlat] Desktop got: {m}"); desktopMsg.TrySetResult(m); };
                Task.Run(async () => { await Task.Delay(500); ch.Send("from desktop"); LogStatus("[Signaling XPlat] Desktop sent"); });
            };
            handler.OnPeerConnection += (_, id) => LogStatus($"[Signaling XPlat] Desktop peer: {id[..8]}");

            var desktopPeerId = NewRandomPeerId();
            await using var desktopClient = new SpawnDev.RTC.Signaling.TrackerSignalingClient(trackerUrl, desktopPeerId);
            var desktopConnected = new TaskCompletionSource();
            desktopClient.OnConnected += () => { LogStatus("[Signaling XPlat] Desktop connected"); desktopConnected.TrySetResult(); };
            desktopClient.OnWarning += w => LogStatus($"[Signaling XPlat] Desktop warning: {w}");

            desktopClient.Subscribe(room, handler);
            // Drive connect + await it — otherwise AnnounceAsync just queues against
            // an unopened socket, and browser may announce first on localhost.
            await desktopClient.AnnounceAsync(room, new SpawnDev.RTC.Signaling.AnnounceOptions { Event = "started", NumWant = 5 });
            if (await Task.WhenAny(desktopConnected.Task, Task.Delay(10_000)) != desktopConnected.Task)
                throw new Exception("Desktop client did not report OnConnected within 10s");
            // Give the queued announce a moment to flush onto the open socket.
            await Task.Delay(500);

            // Browser peer via raw JS - same wire format as plain WebTorrent tracker clients.
            // The room hash must match how RoomKey.FromString does it: SHA-1 of UTF-8 bytes of the
            // room name with NO normalization (no .trim(), no .toLowerCase()).
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
                            new TextEncoder().encode('{roomName}')));
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

                        // Persistent handler loop: the WebTorrent tracker relays MANY messages per
                        // announce (desktop's NumWant=5 pushes up to 5 offers to us, plus the 1 answer
                        // to our own offer). Single-shot ws.onmessage drops everything after the first
                        // message. We loop until our DC opens or a timeout fires.
                        // Incoming offers are answered on fresh RTCPeerConnections (one per offer_id)
                        // so our original pc's state isn't disturbed. Incoming answers matching our
                        // offerId complete the handshake on pc.
                        const peerPCs = new Map();
                        ws.onmessage = async (e) => {{
                            try {{
                                const m = JSON.parse(e.data);
                                if (m.answer && m.offer_id === offerId) {{
                                    await pc.setRemoteDescription({{ type: 'answer', sdp: m.answer.sdp }});
                                }} else if (m.offer && !peerPCs.has(m.offer_id)) {{
                                    const pc2 = new RTCPeerConnection({{ iceServers: [{{ urls: 'stun:stun.l.google.com:19302' }}] }});
                                    pc2.onicecandidate = () => {{}};
                                    peerPCs.set(m.offer_id, pc2);
                                    await pc2.setRemoteDescription({{ type: 'offer', sdp: m.offer.sdp }});
                                    const ans = await pc2.createAnswer();
                                    await pc2.setLocalDescription(ans);
                                    ws.send(JSON.stringify({{
                                        action: 'announce',
                                        info_hash: infoHash,
                                        peer_id: peerId,
                                        to_peer_id: m.peer_id,
                                        answer: {{ type: 'answer', sdp: ans.sdp }},
                                        offer_id: m.offer_id
                                    }}));
                                }}
                            }} catch (err) {{ console.error('ws msg loop:', err); }}
                        }};

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

            LogStatus($"[Signaling XPlat] Browser result: {browserResult}");

            var dr = await Task.WhenAny(desktopMsg.Task, Task.Delay(20000));
            var desktopResult = dr == desktopMsg.Task ? "OK:" + await desktopMsg.Task : "TIMEOUT";
            LogStatus($"[Signaling XPlat] Desktop result: {desktopResult}");

            handler.Dispose();

            if (!browserResult.StartsWith("OK:")) throw new Exception($"Browser: {browserResult}");
            if (!desktopResult.StartsWith("OK:")) throw new Exception($"Desktop: {desktopResult}");

            LogStatus("[Signaling XPlat] SUCCESS - Browser and desktop exchanged messages via new signaling client!");
        }

        private static byte[] NewRandomPeerId()
        {
            var buf = new byte[20];
            System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
            return buf;
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