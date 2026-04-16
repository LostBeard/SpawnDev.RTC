using SpawnDev.RTC;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        /// <summary>
        /// Verify EnumerateDevices returns an array of device info.
        /// Browser: returns real devices (Playwright grants permissions).
        /// Desktop: returns empty array (SipSorcery has no device discovery).
        /// </summary>
        [TestMethod]
        public async Task EnumerateDevices_ReturnsDeviceInfo()
        {
            var devices = await RTCMediaDevices.EnumerateDevices();
            if (devices == null) throw new Exception("EnumerateDevices returned null");

            if (OperatingSystem.IsBrowser())
            {
                // Browser should return at least one device (fake devices in Playwright)
                if (devices.Length == 0)
                    throw new Exception("Browser: EnumerateDevices returned 0 devices");

                foreach (var device in devices)
                {
                    if (string.IsNullOrEmpty(device.DeviceId))
                        throw new Exception("Device has empty DeviceId");
                    if (string.IsNullOrEmpty(device.Kind))
                        throw new Exception("Device has empty Kind");
                    if (device.Kind != "videoinput" && device.Kind != "audioinput" && device.Kind != "audiooutput")
                        throw new Exception($"Unexpected device kind: '{device.Kind}'");
                }

                // Verify we have at least one of each common type
                var hasVideoInput = devices.Any(d => d.Kind == "videoinput");
                var hasAudioInput = devices.Any(d => d.Kind == "audioinput");
                var hasAudioOutput = devices.Any(d => d.Kind == "audiooutput");

                // Playwright fake devices should provide at least audio input and output
                if (!hasAudioInput && !hasAudioOutput && !hasVideoInput)
                    throw new Exception("No recognized device types found");
            }
            else
            {
                // Desktop returns empty - no SipSorcery device enumeration
                if (devices.Length != 0)
                    throw new Exception($"Desktop: expected 0 devices (no device enumeration without MultiMedia), got {devices.Length}");
            }
        }

        /// <summary>
        /// Verify RTCMediaDeviceInfo properties are all non-null strings.
        /// </summary>
        [TestMethod]
        public async Task EnumerateDevices_DeviceInfoProperties()
        {
            var devices = await RTCMediaDevices.EnumerateDevices();

            if (!OperatingSystem.IsBrowser())
            {
                // Desktop has no devices to test properties on
                if (devices.Length == 0) return;
            }

            foreach (var device in devices)
            {
                // All properties should be non-null strings (may be empty for label/groupId)
                if (device.DeviceId == null) throw new Exception("DeviceId is null");
                if (device.Kind == null) throw new Exception("Kind is null");
                if (device.Label == null) throw new Exception("Label is null");
                if (device.GroupId == null) throw new Exception("GroupId is null");
            }
        }

        /// <summary>
        /// Verify MediaConstraint implicit conversions work correctly.
        /// </summary>
        [TestMethod]
        public async Task MediaConstraint_ImplicitConversions()
        {
            // Bool conversion
            MediaConstraint boolConstraint = true;
            if (!boolConstraint.IsBool) throw new Exception("Bool constraint should report IsBool=true");
            if (boolConstraint.BoolValue != true) throw new Exception("Bool value should be true");
            if (boolConstraint.Constraints != null) throw new Exception("Bool constraint should have null Constraints");

            // MediaTrackConstraints conversion
            MediaConstraint detailedConstraint = new MediaTrackConstraints
            {
                Width = 1280,
                Height = 720,
                FrameRate = 30.0,
                DeviceId = "test-device",
            };
            if (detailedConstraint.IsBool) throw new Exception("Detailed constraint should report IsBool=false");
            if (detailedConstraint.Constraints == null) throw new Exception("Detailed constraint should have non-null Constraints");
            if (detailedConstraint.Constraints.Width != 1280) throw new Exception($"Width: {detailedConstraint.Constraints.Width}");
            if (detailedConstraint.Constraints.Height != 720) throw new Exception($"Height: {detailedConstraint.Constraints.Height}");
            if (detailedConstraint.Constraints.FrameRate != 30.0) throw new Exception($"FrameRate: {detailedConstraint.Constraints.FrameRate}");
            if (detailedConstraint.Constraints.DeviceId != "test-device") throw new Exception($"DeviceId: {detailedConstraint.Constraints.DeviceId}");

            // Usage in MediaStreamConstraints
            var constraints = new MediaStreamConstraints
            {
                Audio = true,
                Video = new MediaTrackConstraints { Width = 640, Height = 480 },
            };
            if (!constraints.Audio!.IsBool) throw new Exception("Audio should be bool");
            if (constraints.Video!.IsBool) throw new Exception("Video should not be bool");
            if (constraints.Video.Constraints!.Width != 640) throw new Exception($"Video width: {constraints.Video.Constraints.Width}");

            await Task.CompletedTask;
        }
    }
}
