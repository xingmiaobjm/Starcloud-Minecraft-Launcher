// 皮肤库契约：账户只记 skin_id，皮肤文件由皮肤库统一管。
//
// 与账户的关系：删掉库里的皮肤时账户引用会被清空（AccountStore.ClearSkinReference），
// 契约只描述"库本身能做什么"，不掺账户逻辑——换实现（云皮肤库、共享目录）不影响调用点。

using Starcloud.Core.Services;

namespace Starcloud.Core.Kernel.Contracts;

public interface ISkinLibrary
{
    /// <summary>实现名（设置 / 插件页展示）</summary>
    string DisplayName { get; }

    /// <summary>库内全部皮肤，按加入时间倒序</summary>
    IReadOnlyList<SkinEntry> List();

    /// <summary>按 id 查一条；没有返回 null</summary>
    SkinEntry? Find(string id);

    /// <summary>读皮肤 PNG 原始字节；文件缺失返回 null</summary>
    byte[]? ReadBytes(string id);

    /// <summary>解析实际使用的型号（auto 会按纹理判断纤细手臂）</summary>
    string ResolveModel(string id, string? overrideModel = null);

    /// <summary>导入一张 PNG（校验尺寸与可解码性），失败返回错误文案</summary>
    (SkinEntry? Entry, string? Error) ImportBytes(byte[] png, string? name,
        string source = "file", string? sourceUrl = null);

    /// <summary>改名</summary>
    (bool Ok, string? Error) Rename(string id, string name);

    /// <summary>改型号（auto / classic / slim）</summary>
    bool SetModel(string id, string model);

    /// <summary>删除（文件一并删掉）</summary>
    bool Delete(string id);
}
