namespace VibeCode.AgentStatus.Mcp.Contracts;

public sealed record AttackTactic(string Id, string Name, string ShortName);

// One catalog shared by the reporting contract and desktop adapter. Labels are not an execution plan.
// Enterprise ATT&CK v19.1: https://attack.mitre.org/tactics/enterprise/
public static class AttackTactics
{
    public const string Version = "Enterprise ATT&CK v19.1";
    public static IReadOnlyList<AttackTactic> All { get; } = Array.AsReadOnly(new[]
    {
        new AttackTactic("TA0043", "Reconnaissance", "REC"),
        new AttackTactic("TA0042", "Resource Development", "RES"),
        new AttackTactic("TA0001", "Initial Access", "INI"),
        new AttackTactic("TA0002", "Execution", "EXE"),
        new AttackTactic("TA0003", "Persistence", "PER"),
        new AttackTactic("TA0004", "Privilege Escalation", "PRI"),
        new AttackTactic("TA0005", "Stealth", "STL"),
        new AttackTactic("TA0112", "Defense Impairment", "DEF"),
        new AttackTactic("TA0006", "Credential Access", "CRE"),
        new AttackTactic("TA0007", "Discovery", "DIS"),
        new AttackTactic("TA0008", "Lateral Movement", "LAT"),
        new AttackTactic("TA0009", "Collection", "COL"),
        new AttackTactic("TA0011", "Command and Control", "C2"),
        new AttackTactic("TA0010", "Exfiltration", "EXF"),
        new AttackTactic("TA0040", "Impact", "IMP"),
    });
}
