// 【拆分】组合文件格式（combinations\*.xml）。（从原 AgentHost.cs 原样搬出，逻辑未改）

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

// Versioned, ordered slot definitions. Session IDs are persistent, independent of
// family IDs used by the host. No terminal history or credentials are serialized.
internal static class SlotProfiles
{
    public static string DirectoryPath { get { return Path.Combine(Path.GetDirectoryName(HostForm.StatusPath), "combinations"); } }
    public static string LastPath { get { return Path.Combine(DirectoryPath, "last.xml"); } }
    public static void Save(string path, string name, IList<AgentKind> slots, int layout)
    {
        var doc = new XmlDocument();
        XmlElement root = doc.CreateElement("combination"); doc.AppendChild(root);
        root.SetAttribute("version", "1"); root.SetAttribute("name", name); root.SetAttribute("layout", layout.ToString());
        foreach (AgentKind k in slots)
        {
            if (Catalog.ById(k.FamilyId) == null) continue;
            var e = doc.CreateElement("slot");
            e.SetAttribute("id", k.Id); e.SetAttribute("family", k.FamilyId);
            e.SetAttribute("cli", k.Cli ? "true" : "false");
            e.SetAttribute("workspace", k.Workspace ?? ""); root.AppendChild(e);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string tmp = path + ".tmp";
        doc.Save(tmp);
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
        else File.Move(tmp, path);
    }
    public static List<AgentKind> Load(string path, out string name, out int layout)
    {
        var doc = new XmlDocument(); doc.XmlResolver = null;
        using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc.Load(reader);
        XmlElement root = doc.DocumentElement;
        if (root == null || root.Name != "combination" || root.GetAttribute("version") != "1") throw new InvalidDataException("组合格式不支持");
        name = root.GetAttribute("name"); int.TryParse(root.GetAttribute("layout"), out layout);
        var result = new List<AgentKind>(); var used = new HashSet<string>();
        foreach (XmlNode node in root.ChildNodes)
        {
            var e = node as XmlElement;
            if (e == null || e.Name != "slot") continue;
            AgentKind family = Catalog.ById(e.GetAttribute("family"));
            if (family == null) throw new InvalidDataException("组合包含未知 Agent");
            bool cli = e.GetAttribute("cli") == "true";
            string id = cli ? e.GetAttribute("id") : family.Id;
            Guid guid;
            if (cli && (!id.StartsWith("cli-", StringComparison.Ordinal) || !Guid.TryParseExact(id.Substring(4), "N", out guid)))
                throw new InvalidDataException("CLI 会话标识无效");
            if (!used.Add(id) || result.Count >= HostForm.MaxSlotsAny) throw new InvalidDataException("组合格子重复或超过上限");
            result.Add(cli ? AgentKind.Session(family, id, e.GetAttribute("workspace")) : family);
        }
        return result;
    }
    public static bool Swap<T>(IList<T> slots, T source, T target)
    {
        int a = slots.IndexOf(source), b = slots.IndexOf(target);
        if (a < 0 || b < 0 || a == b) return false;
        slots[a] = target; slots[b] = source; return true;
    }
}
