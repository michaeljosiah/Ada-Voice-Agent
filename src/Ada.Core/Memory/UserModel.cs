namespace Ada.Core;

/// <summary>
/// <c>USER.md</c> — a single, evolving model of the user that Ada updates as she learns preferences
/// and patterns (spec §9). A plain file you can open and rewrite.
/// </summary>
public sealed class UserModel(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(AdaPaths.DataDir, "USER.md");

    public string Read() => File.Exists(_path) ? File.ReadAllText(_path) : string.Empty;

    public void Write(string content)
    {
        AdaPaths.EnsureDataDir();
        File.WriteAllText(_path, content);
    }

    public void Append(string line)
    {
        AdaPaths.EnsureDataDir();
        File.AppendAllText(_path, line.TrimEnd() + Environment.NewLine);
    }
}
