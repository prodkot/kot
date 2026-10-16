using Kot.Core;
namespace Kot.Tests;

static class LogTests
{
    public static void Run(Action<bool, string> check)
    {
        Node Server(string password, string host) => Subscriptions.ParseNode($"trojan://{password}@{host}:443?sni={host}");
        var log = new RedactedSessionLog(4096);
        var old = Server("old-private-password", "old.private.example");
        log.Register([old], @"C:\Users\Private");
        log.Write("old-private-password", "connect old.private.example old-private-password at NativeCore.cs:line 42");
        // Mutating a profile object cannot change the privacy rules already in use.
        old.Outbound["password"] = "mutated-password";
        check(!log.Clean("old-private-password").Contains("old-private-password"), "log privacy owns values independently of mutable nodes");
        for (int i = 0; i < 500; i++)
            log.Register([Server("current-password-" + i, "current-" + i + ".private.example")]);
        check(log.Clean("old-private-password") == "old-private-password" && log.Clean("current-password-499") == "[скрыто]",
            "profile refresh replaces historical privacy values while retaining the current server");
        check(!log.Snapshot().Contains("old-private-password") && !log.Snapshot().Contains("old.private.example") && log.Snapshot().Contains("NativeCore.cs:line 42"),
            "old journal entries stay sanitized after old profile values are released");
        string telegram = new string('b', 32), controller = new string('c', 64), hwid = new string('a', 64);
        log.Write("credentials", telegram + " dd" + telegram + " " + controller + " " + hwid + " https://private.example/sub?token=password https%3A%2F%2Fprivate.example%2Fsub%3Ftoken%3Dpassword");
        string clean = log.Snapshot();
        check(!clean.Contains(telegram) && !clean.Contains(controller) && !clean.Contains(hwid) && !clean.Contains("token") && !clean.Contains("private.example"),
            "generated credentials, historical Telegram secrets, HWID and encoded URLs need no secret history");
        log.Register([]); log.Error("failure", new IOException("actual error at NativeCore.cs:line 43"));
        check(log.Snapshot().Contains("IOException: actual error") && log.Snapshot().Contains("NativeCore.cs:line 43"), "sanitized journal preserves exception details");
        log.Write("overflow", new string('x', 5000));
        check(log.Snapshot().Contains("Начало журнала удалено") && log.Snapshot().Length < 4300, "sanitized journal remains bounded after truncation");
        var late = new RedactedSessionLog(); late.Write("registration", "late-private-password"); late.Register([Server("late-private-password", "late.private.example")]); late.Register([]);
        check(!late.Snapshot().Contains("late-private-password"), "late profile registration sanitizes stored entries before values are released");
    }
}
