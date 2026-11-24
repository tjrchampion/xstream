using System.Text;
using SIPSorcery.Net;
using XStream.Net.Stun;

/// <summary>Our STUN connectivity checks (used to measure round trip) against SIPSorcery's STUN, which the Xbox accepts.</summary>
static class StunTests
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("STUN check: SIPSorcery parses ours and its integrity and fingerprint check out", () =>
        {
            var id = Encoding.ASCII.GetBytes("abcdefghijkl");
            var buf = new byte[256];
            int len = StunBinding.BuildRequest(buf, id, "REMOTE:local", "remote-password-123456", 0x6E7F1EFF, controlling: true, 0x0123456789ABCDEF);
            var msg = STUNMessage.ParseSTUNMessage(buf, len) ?? throw new Exception("not parsed");
            if (msg.Header.MessageType != STUNMessageTypesEnum.BindingRequest) throw new Exception($"type {msg.Header.MessageType}");
            if (!msg.isFingerprintValid) throw new Exception("fingerprint");
            if (!msg.CheckIntegrity(Encoding.UTF8.GetBytes("remote-password-123456"))) throw new Exception("integrity");
            if (msg.CheckIntegrity(Encoding.UTF8.GetBytes("wrong"))) throw new Exception("integrity accepts a wrong key");
            var user = msg.Attributes.First(a => a.AttributeType == STUNAttributeTypesEnum.Username);
            if (Encoding.UTF8.GetString(user.Value) != "REMOTE:local") throw new Exception("username");
            if (!msg.Attributes.Any(a => a.AttributeType == STUNAttributeTypesEnum.IceControlling)) throw new Exception("ICE-CONTROLLING");
        }),
        ("STUN check: SIPSorcery's success response is recognised and matched", () =>
        {
            var resp = new STUNMessage(STUNMessageTypesEnum.BindingSuccessResponse);
            resp.Header.TransactionId = Encoding.ASCII.GetBytes("abcdefghijkl");
            resp.AddXORMappedAddressAttribute(System.Net.IPAddress.Parse("192.168.1.20"), 9002);
            var bytes = resp.ToByteBufferStringKey("remote-password-123456", true);
            if (!StunBinding.IsBindingSuccess(bytes, out var tx)) throw new Exception("not recognised");
            if (Encoding.ASCII.GetString(tx) != "abcdefghijkl") throw new Exception("transaction ID");
            var req = new STUNMessage(STUNMessageTypesEnum.BindingRequest);
            req.Header.TransactionId = Encoding.ASCII.GetBytes("abcdefghijkl");
            if (StunBinding.IsBindingSuccess(req.ToByteBufferStringKey("x", true), out _)) throw new Exception("request taken for a response");
        }),
        ("STUN CRC-32 matches the standard check value", () =>
        {
            if (StunBinding.Crc32(Encoding.ASCII.GetBytes("123456789")) != 0xCBF43926) throw new Exception("crc");
        }),
    ];
}
