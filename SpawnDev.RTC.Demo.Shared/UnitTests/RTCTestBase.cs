using SpawnDev.BlazorJS.Cryptography;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{

    public abstract partial class RTCTestBase
    {
        // Reusable test setup can go here, e.g., initializing shared resources or services.
        protected RTCTestBase()
        {

        }

        [TestMethod]
        public async Task UnitTestSetupWorking()
        {
            await Task.Delay(10000);
        }
    }
}
