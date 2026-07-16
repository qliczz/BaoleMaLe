namespace BaoleMaLe;

using Dalamud.Interface.Textures;
using System.Collections.Generic;

/// <summary>
/// 技能图标缓存：根据 actionId 从游戏内 Action 表取图标 ID，再通过 ITextureProvider 加载，
/// 返回可用于 ImGui.Image 的 ImGuiHandle。同一图标只加载一次。
/// </summary>
public sealed class IconCache
{
    private readonly ITextureProvider tex;
    private readonly Dictionary<uint, ISharedImmediateTexture?> cache = new();
    private readonly object lk = new();

    public IconCache(ITextureProvider tex)
    {
        this.tex = tex;
    }

    /// <summary>返回技能图标的 ImTextureID（失败/无图标返回 default/Null）。</summary>
    public ImTextureID GetHandle(uint actionId)
    {
        uint iconId = 0;
        try
        {
            var row = DalamudApi.DataManager?.GetExcelSheet<Action>()?.GetRow(actionId);
            if (row != null)
                iconId = row.Value.Icon;
        }
        catch { return default; }

        if (iconId == 0)
            return default;

        ISharedImmediateTexture? tex2;
        lock (lk)
        {
            if (!this.cache.TryGetValue(iconId, out tex2))
            {
                try { tex2 = this.tex.GetFromGameIcon(new GameIconLookup(iconId)); }
                catch { tex2 = null; }
                this.cache[iconId] = tex2;
            }
        }
        return tex2?.GetWrapOrDefault()?.Handle ?? default;
    }

    public void Dispose()
    {
        lock (lk) this.cache.Clear();
    }
}
