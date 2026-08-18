using Microsoft.Playwright;
using SpawnDev.UnitTesting;
using System.Diagnostics;

namespace PlaywrightMultiTest
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class Tests : PageTest
    {
        public static IEnumerable<TestCaseData> TestCases => ProjectRunner.Instance.TestCases;

        [OneTimeSetUp]
        public async Task StartApp()
        {
            await ProjectRunner.Instance.StartUp();
        }

        [Test, TestCaseSource(nameof(TestCases))]
        public async Task RunTest(ProjectTest test)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (test.Project is TestableBlazorWasm blazorProj)
                {
                    await test.TestFunc(blazorProj.Page);
                    if (test.Result == TestResult.Unsupported)
                    {
                        sw.Stop();
                        TestResultsWriter.RecordResult(test.Name, "Skip", test.ResultMessage, sw.Elapsed.TotalMilliseconds);
                        Assert.Ignore(test.ResultMessage!);
                    }
                }
                else if (test.Project is TestableConsole)
                {
                    await test.TestFunc(null!);
                    if (test.Result == TestResult.Unsupported)
                    {
                        sw.Stop();
                        TestResultsWriter.RecordResult(test.Name, "Skip", test.ResultMessage, sw.Elapsed.TotalMilliseconds);
                        Assert.Ignore(test.ResultMessage!);
                    }
                }
                sw.Stop();
                TestResultsWriter.RecordResult(test.Name, "Pass", null, sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is IgnoreException or SuccessException or InconclusiveException)
            {
                // Assert.Ignore/Pass/Inconclusive report an outcome by throwing. The row for this
                // test was already written immediately before the assert, so letting it fall into
                // the failure handler below recorded every skipped test a second time as "Fail" -
                // which is why a run with 3 skips reported 7 failures instead of 4.
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                TestResultsWriter.RecordResult(test.Name, "Fail", ex.Message, sw.Elapsed.TotalMilliseconds);
                throw;
            }
        }

        [OneTimeTearDown]
        public async Task StopApp()
        {
            TestResultsWriter.WriteFinalSummary();
            await ProjectRunner.Instance.Shutdown();
        }
    }
}
