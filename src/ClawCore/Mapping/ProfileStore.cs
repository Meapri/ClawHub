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
        if (missing.Count > 0) _data.Profiles.AddRange(missing);

        // 기본·데스크톱 프로필은 늘 맨 앞에 있다 (없으면 새로)
        var changed = missing.Count > 0;
        foreach (var (id, make) in new (string, Func<MappingProfile>)[] { (MappingProfile.DesktopId, MappingPresets.NewDesktop), (MappingProfile.DefaultId, MappingPresets.NewDefault) })
        {
            if (_data.Profiles.Any(p => p.Id == id)) continue;
            _data.Profiles.Insert(0, make());
            changed = true;
        }
        if (changed) Save();
    }

    public IReadOnlyList<MappingProfile> All => _data.Profiles;

    public MappingProfile Default => Find(MappingProfile.DefaultId)!;
    public MappingProfile Desktop => Find(MappingProfile.DesktopId)!;

    public MappingProfile? Find(string? id) => id is null ? null : _data.Profiles.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// 게임에 실제로 쓸 프로필: 게임 전용 → (없으면) 기본 프로필 → (기본이 비어 있으면) 없음.
    /// 게임의 값이 <see cref="MappingProfile.NoneId"/>면 기본 프로필도 쓰지 않는다.
    /// </summary>
    public MappingProfile? ForGame(string? gameProfileId) => gameProfileId == MappingProfile.NoneId
        ? null
        : Find(gameProfileId) is { IsSystem: false } own ? own
        : Default is { IsEmpty: false } d ? d : null;

    /// <summary>게임 쪽에 보여 줄 이름 ("기본 프로필 따름" · "사용 안 함" · 프로필 이름).</summary>
    public string LabelForGame(string? gameProfileId) => gameProfileId == MappingProfile.NoneId
        ? "사용 안 함 (게임패드 그대로)"
        : Find(gameProfileId) is { IsSystem: false } own ? own.Name
        : Default.IsEmpty ? "기본 프로필 (비어 있음 · 게임패드 그대로)" : $"기본 프로필 · {Default.Name}";

    /// <summary>게임 프로필을 차례로 바꿀 때 순서: 기본 따름 → 내 프로필·템플릿 → 사용 안 함 → 처음으로.</summary>
    public string? NextForGame(string? current)
    {
        List<string?> order = [null, .. _data.Profiles.Where(p => !p.IsSystem).Select(p => p.Id), MappingProfile.NoneId];
        var i = order.IndexOf(current is null || current == MappingProfile.NoneId || Find(current) is { IsSystem: false } ? current : null);
        return order[(i + 1) % order.Count];
    }

    public void Upsert(MappingProfile profile)
    {
        var i = _data.Profiles.FindIndex(p => p.Id == profile.Id);
        if (i >= 0) _data.Profiles[i] = profile; else _data.Profiles.Add(profile);
        Save();
    }

    public void Delete(string id)
    {
        if (id is MappingProfile.DefaultId or MappingProfile.DesktopId) return; // 기본·데스크톱은 지울 수 없다
        _data.Profiles.RemoveAll(p => p.Id == id);
        if (MappingPresets.All.Any(p => p.Id == id)) _data.DeletedPresets.Add(id);
        Save();
    }

    public void Save() => JsonStore.Save(FilePath, _data);
}
