namespace ProjectHub.Edge.Desktop;

internal static class ProjectSkills
{
    internal static string Install(string workspace, IReadOnlyCollection<string> tools)
    {
        var selected = new List<string>();
        if (tools.Contains("list_projects") && (tools.Contains("get_project_snapshot") ||
            tools.Contains("search_project_knowledge") || tools.Contains("list_assets"))) selected.Add(UserSkills.Init);
        if (tools.Contains("get_project_snapshot")) selected.Add("nodicarta-context");
        if (tools.Contains("submit_knowledge_commit")) selected.Add("nodicarta-sync");
        if (tools.Contains("list_feedback") || tools.Contains("list_tasks")) selected.Add("nodicarta-inbox");
        if (tools.Contains("list_assets")) selected.Add("nodicarta-assets");
        if (selected.Count == 0) return "当前工具范围没有对应的项目 Skill。";
        var agents = Path.Combine(workspace, ".agents");
        var skills = Path.Combine(agents, "skills");
        if (!Directory.Exists(workspace)) throw new DirectoryNotFoundException("当前绑定目录已移动。");
        for (var dir = new DirectoryInfo(skills); dir != null; dir = dir.Parent)
            if (dir.LinkTarget != null || (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                throw new IOException("项目的技能目录路径包含链接，请手动安装。");
        Directory.CreateDirectory(skills);
        var installed = new List<string>();
        var kept = new List<string>();
        foreach (var name in selected)
        {
            var target = Path.Combine(skills, name);
            if (Directory.Exists(target) || File.Exists(target) || new DirectoryInfo(target).LinkTarget != null)
            {
                kept.Add(name);
                continue;
            }
            var source = Path.Combine(AppContext.BaseDirectory, "skills", name);
            if (!File.Exists(Path.Combine(source, "SKILL.md"))) throw new IOException($"发行包中缺少 {name}。");
            var stage = Directory.CreateDirectory(Path.Combine(agents, $".projecthub-{Guid.NewGuid():N}"));
            try
            {
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var copy = Path.Combine(stage.FullName, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(file, copy);
                }
                try { Directory.Move(stage.FullName, target); installed.Add(name); }
                catch (IOException) when (Directory.Exists(target)) { kept.Add(name); }
            }
            finally { if (Directory.Exists(stage.FullName)) Directory.Delete(stage.FullName, true); }
        }
        var result = new List<string>();
        if (installed.Count > 0) result.Add("已安装 " + string.Join("、", installed));
        if (kept.Count > 0) result.Add("已保留原目录 " + string.Join("、", kept));
        return string.Join("。", result) + "。";
    }
}
