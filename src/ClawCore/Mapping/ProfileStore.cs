namespace ClawCore.Mapping;

public sealed class ProfileStore
{
    private sealed class Data
    {
        public List<MappingProfile> Profiles { get; set; } = [];
        public List<string> DeletedPresets { get; set; } = [];
    }

    private static string FilePath => Path.Combine(JsonStore.UserDir, "profiles.json");

    private readonly Data _data = JsonStore.Load<Data>(FilePath);

    public ProfileStore()
    {
        // 템플릿은 없을 때만 추가 (사용자가 고친 건 덮어쓰지 않고, 지운 건 되살리지 않음)
        var missing = MappingPresets.All
            .Where(p => _data.Profiles.All(x => x.Id != p.Id) && !_data.DeletedPresets.Contains(p.Id)).ToList();
        if (missing.Count > 0)
        {
            _data.Profiles.AddRange(missing);
            Save();
        }
    }

    public IReadOnlyList<MappingProfile> All => _data.Profiles;

    public MappingProfile? Find(string? id) => id is null ? null : _data.Profiles.FirstOrDefault(p => p.Id == id);

    public void Upsert(MappingProfile profile)
    {
        var i = _data.Profiles.FindIndex(p => p.Id == profile.Id);
        if (i >= 0) _data.Profiles[i] = profile; else _data.Profiles.Add(profile);
        Save();
    }

    public void Delete(string id)
    {
        _data.Profiles.RemoveAll(p => p.Id == id);
        if (MappingPresets.All.Any(p => p.Id == id)) _data.DeletedPresets.Add(id);
        Save();
    }

    public void Save() => JsonStore.Save(FilePath, _data);
}
