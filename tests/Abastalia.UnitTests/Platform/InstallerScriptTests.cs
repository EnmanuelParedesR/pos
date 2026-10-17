using Shouldly;

namespace Abastalia.UnitTests.Platform;

/// <summary>TK-160 (clean VM test): two mistakes in deploy/installer/abastalia.iss that only ISCC or a silent install found.</summary>
public sealed class InstallerScriptTests
{
    private static readonly string Code = File.ReadAllText(Repo(@"deploy\installer\abastalia.iss")).Split("[Code]")[1];

    [Fact]
    public void A_brace_comment_has_no_brace_inside()
    {
        // Pascal ends a { comment } at the first }: "{app}" inside one broke the compile.
        for (int i = 0, inString = 0; i < Code.Length; i++)
        {
            if (Code[i] == '\'') inString ^= 1;
            if (inString == 1 || Code[i] != '{') continue;
            var end = Code.IndexOf('}', i);
            Code[(i + 1)..end].ShouldNotContain("{", customMessage: $"comment at {i}: {Code[i..end]}");
            i = end;
        }
    }

    [Fact]
    public void Setup_never_waits_on_a_message_box_nobody_sees()
    {
        // /SUPPRESSMSGBOXES (the SYSTEM update task) only answers SuppressibleMsgBox: a plain MsgBox hangs a silent install forever.
        // The uninstall part asks only when not silent.
        var setup = Code.Split("function RemoveData")[0];
        setup.Replace("SuppressibleMsgBox(", "").ShouldNotContain("MsgBox(");
    }

    private static string Repo(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Abastalia.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Abastalia.slnx not found"), relative);
    }
}
