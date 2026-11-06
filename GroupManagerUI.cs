using Alife.Framework;
using Microsoft.AspNetCore.Components.Rendering;

namespace Suran.GroupManager;

/// <summary>群聊管家 的设置页。内容由 SuranUiKit 按配置项自动分组生成。</summary>
public sealed class GroupManagerUI : ModuleUIBase<GroupManagerModule, GroupManagerConfig>
{
    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.AddContent(0, "配置未加载");
            return;
        }
        SuranUiKit.Render(this, StateHasChanged, b, Configuration, "群聊管家", "禁言、踢人、名片、撤回、公告与成员变动；高危功能各有独立开关", "#8b5cf6");
    }
}
