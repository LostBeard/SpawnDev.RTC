using System.Security.Cryptography;
using System.Text;
using SpawnDev.RTC.Signaling;
using SpawnDev.UnitTesting;

namespace SpawnDev.RTC.Demo.Shared.UnitTests
{
    public abstract partial class RTCTestBase
    {
        // ---- RoomKey: fundamental invariants ----

        [TestMethod]
        public async Task RoomKey_Length_Is20()
        {
            if (RoomKey.Length != 20) throw new Exception($"Expected Length=20, got {RoomKey.Length}");
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task RoomKey_FromBytes_RoundTrips()
        {
            var src = new byte[20];
            for (int i = 0; i < 20; i++) src[i] = (byte)(i * 13);
            var key = RoomKey.FromBytes(src);
            var back = key.ToArray();
            for (int i = 0; i < 20; i++)
                if (back[i] != src[i]) throw new Exception($"Byte {i} mismatch: {back[i]} != {src[i]}");
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task RoomKey_FromBytes_DefensiveCopy()
        {
            var src = new byte[20];
            src[0] = 0xAB;
            var key = RoomKey.FromBytes(src);
            src[0] = 0xCD;
            if (key.AsSpan()[0] != 0xAB) throw new Exception("FromBytes did not copy; mutation leaked into key");
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task RoomKey_FromBytes_RejectsWrongLength()
        {
            try { RoomKey.FromBytes(new byte[19]); throw new Exception("Expected ArgumentException for 19 bytes"); }
            catch (ArgumentException) { }
            try { RoomKey.FromBytes(new byte[21]); throw new Exception("Expected ArgumentException for 21 bytes"); }
            catch (ArgumentException) { }
            try { RoomKey.FromBytes(Array.Empty<byte>()); throw new Exception("Expected ArgumentException for empty"); }
            catch (ArgumentException) { }
            await Task.CompletedTask;
        }

        // ---- RoomKey: FromString hashing ----

        [TestMethod]
        public async Task RoomKey_FromString_IsDeterministic()
        {
            var a = RoomKey.FromString("my-lobby-42");
            var b = RoomKey.FromString("my-lobby-42");
            if (a != b) throw new Exception("Same string produced different keys");
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task RoomKey_FromString_DifferentInputsProduceDifferentKeys()
        {
            var a = RoomKey.FromString("room-A");
            var b = RoomKey.FromString("room-B");
            if (a == b) throw new Exception("Different strings produced same key");
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task RoomKey_FromString_IsSha1OfUtf8()
        {
            const string name = "spawndev-rtc-test-room";
            var expected = SHA1.HashData(Encoding.UTF8.GetBytes(name));
            var key = RoomKey.FromString(name);
            var actual = key.ToArray();
            for (int i = 0; i < 20; i++)
                if (actual[i] != expected[i]) throw new Exception($"Byte {i} differs from SHA-1(UTF-8(name))");
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task RoomKey_FromString_IsCaseSensitive()
        {
            var lo = RoomKey.FromString("lobby");
            var up = RoomKey.FromString("LOBBY");
            if (lo == up) throw new Exception("FromString unexpectedly normalized case");
            await Task.CompletedTask;
        }

        // ---- RoomKey: FromGuid ----

        [TestMethod]
        public async Task RoomKey_FromGuid_IsDeterministic()
        {
            var g = new Guid("11112222-3333-4444-5555-666677778888");
            var a = RoomKey.FromGuid(g);
            var b = RoomKey.FromGuid(g);
            if (a != b) throw new Exception("Same Guid produced different keys");
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task RoomKey_FromGuid_LastFourBytesAreZero()
        {
            var key = RoomKey.FromGuid(Guid.NewGuid());
            var bytes = key.ToArray();
            for (int i = 16; i < 20; i++)
                if (bytes[i] != 0) throw new Exception($"Padding byte {i} is {bytes[i]}, expected 0");
            await Task.CompletedTask;
        }

        // ---- RoomKey: Random ----

        [TestMethod]
        public async Task RoomKey_Random_ProducesDifferentKeys()
        {
            var a = RoomKey.Random();
            var b = RoomKey.Random();
            if (a == b) throw new Exception("Two Random() calls produced the same key - this is astronomically unlikely");
            await Task.CompletedTask;
        }

        // ---- RoomKey: TryParse + ToHex ----

        [TestMethod]
        public async Task RoomKey_ToHex_IsLowercase40Chars()
        {
            var key = RoomKey.FromString("hex-test");
            var hex = key.ToHex();
            if (hex.Length != 40) throw new Exception($"Expected 40 hex chars, got {hex.Length}");
            foreach (var c in hex)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    throw new Exception($"Hex contains non-lowercase-hex char: '{c}'");
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task RoomKey_TryParse_RoundTripsHex()
        {
            var original = RoomKey.Random();
            if (!RoomKey.TryParse(original.ToHex(), out var parsed))
                throw new Exception("TryParse failed on own output");
            if (parsed != original) throw new Exception("Parsed key differs from original");
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task RoomKey_TryParse_AcceptsUppercaseHex()
        {
            var original = RoomKey.Random();
            var hex = original.ToHex().ToUpperInvariant();
            if (!RoomKey.TryParse(hex, out var parsed)) throw new Exception("Uppercase hex rejected");
            if (parsed != original) throw new Exception("Uppercase parse produced different key");
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task RoomKey_TryParse_RejectsInvalid()
        {
            if (RoomKey.TryParse(null, out _)) throw new Exception("Accepted null");
            if (RoomKey.TryParse("", out _)) throw new Exception("Accepted empty");
            if (RoomKey.TryParse("abc", out _)) throw new Exception("Accepted 3-char string");
            if (RoomKey.TryParse(new string('z', 40), out _)) throw new Exception("Accepted non-hex chars");
            if (RoomKey.TryParse(new string('a', 41), out _)) throw new Exception("Accepted 41-char hex");
            await Task.CompletedTask;
        }

        // ---- RoomKey: wire format ----

        [TestMethod]
        public async Task RoomKey_ToWireString_Is20CharsLatin1()
        {
            var src = new byte[20];
            for (int i = 0; i < 20; i++) src[i] = (byte)(i * 7);
            var key = RoomKey.FromBytes(src);
            var wire = key.ToWireString();
            if (wire.Length != 20) throw new Exception($"Expected 20 chars, got {wire.Length}");
            for (int i = 0; i < 20; i++)
                if ((byte)wire[i] != src[i]) throw new Exception($"Wire char {i}: got 0x{(int)wire[i]:X}, expected 0x{src[i]:X}");
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task RoomKey_ToWireString_HandlesHighBytes()
        {
            var src = new byte[20];
            for (int i = 0; i < 20; i++) src[i] = 0xFF;
            var key = RoomKey.FromBytes(src);
            var wire = key.ToWireString();
            foreach (var c in wire)
                if ((byte)c != 0xFF) throw new Exception($"High-byte (0xFF) lost through wire encoding: got 0x{(int)c:X}");
            await Task.CompletedTask;
        }

        // ---- RoomKey: equality and default ----

        [TestMethod]
        public async Task RoomKey_Equality_WorksByValue()
        {
            var a = RoomKey.FromString("alpha");
            var b = RoomKey.FromString("alpha");
            var c = RoomKey.FromString("beta");
            if (!a.Equals(b)) throw new Exception("Equal keys Equals returned false");
            if (a.Equals(c)) throw new Exception("Unequal keys Equals returned true");
            if (!(a == b)) throw new Exception("== operator wrong");
            if (a != b) throw new Exception("!= operator wrong");
            if (a.GetHashCode() != b.GetHashCode()) throw new Exception("Equal keys produced different hash codes");
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task RoomKey_Default_IsDetectable()
        {
            RoomKey def = default;
            if (!def.IsDefault) throw new Exception("default.IsDefault should be true");
            var explicitZero = RoomKey.FromBytes(new byte[20]);
            if (explicitZero.IsDefault) throw new Exception("Explicit all-zero key reported as default");
            await Task.CompletedTask;
        }

        [TestMethod]
        public async Task RoomKey_Default_EqualsAllZeroBytes()
        {
            RoomKey def = default;
            var zero = RoomKey.FromBytes(new byte[20]);
            if (def != zero) throw new Exception("default should equal FromBytes(all-zeros) on the wire");
            if (def.ToHex() != new string('0', 40)) throw new Exception("default ToHex should be 40 zeros");
            if (def.ToWireString().Length != 20) throw new Exception("default ToWireString should have length 20");
            await Task.CompletedTask;
        }
    }
}
