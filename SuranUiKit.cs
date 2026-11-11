using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace Suran.GroupManager;

/// <summary>
/// 通用配置界面，外观对齐 EmotionTTS 那一页（gs- 皮肤：紫色 hero + 左侧色条分区 + 统一控件）。
///
/// 这些插件的配置项命名一直是「分组·名称」（`检索·默认返回条数`、`网络·代理地址`），
/// 说明文字又都写在 Description 里、动辄两三百字。所以这里仍然按 `·` 自动分组，
/// 只是把原来那套通用卡片换成了语音页那套视觉：hero 里有标题、副文案和分组链路，
/// 每个分组是一张带左侧色条的面板，说明缩成标签下一行小字。
///
/// 每个插件只需要在 XxxUI 里调一次 Render。
/// </summary>
public static class SuranUiKit
{
    /// <summary>折叠状态按「配置类型 + 分组」记，默认全展开（对齐语音页那种一览到底的版式）</summary>
    static readonly Dictionary<string, bool> Collapsed = new(StringComparer.Ordinal);
    static readonly Dictionary<string, string> Queries = new(StringComparer.Ordinal);

    /// <summary>
    /// 渲染上下文。ComponentBase.StateHasChanged 是 protected，静态方法调不到，
    /// 只能让调用方把「怎么刷新」作为委托传进来。顺带把 receiver 也放一起，少传一个参数。
    /// </summary>
    sealed class Ctx
    {
        public ComponentBase Host = null!;
        public Action Refresh = static () => { };
        public Action<RenderTreeBuilder, int, string>? RowTail;
    }

    /// <summary>头部动作按钮。给需要"点一下跑个探测"的插件用，不用为两个按钮写一整页 UI</summary>
    public sealed record UiAction(string Label, Func<Task> Run);

    sealed class Field
    {
        public PropertyInfo Prop = null!;
        public string Group = "常规";
        public string Label = "";
        public string Hint = "";
        public int Order;
    }

    public static void Render(ComponentBase host, Action refresh, RenderTreeBuilder b, object config,
        string title, string subtitle, string accent,
        IReadOnlyList<UiAction>? actions = null, string? notice = null,
        Action<RenderTreeBuilder, int, string>? rowTail = null)
    {
        Ctx ctx = new() { Host = host, Refresh = refresh, RowTail = rowTail };
        int i = 0;
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-root");
        b.AddAttribute(i++, "style", "--su-accent:" + accent + ";--su-pink:#eb2f96;--su-pink-soft:#f759ab;");

        InjectStyles(b, ref i);

        string key = config.GetType().FullName ?? "config";
        List<Field> fields = Describe(config);
        string query = Queries.TryGetValue(key, out string? stored) ? stored : "";

        BuildHero(b, ref i, title, subtitle, fields);

        if (actions is { Count: > 0 })
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "su-actions");
            foreach (UiAction action in actions)
            {
                UiAction captured = action;
                b.OpenElement(i++, "button");
                b.AddAttribute(i++, "type", "button");
                b.AddAttribute(i++, "class", "su-act");
                b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(host, async _ =>
                {
                    await captured.Run();
                    refresh();
                }));
                b.AddContent(i++, captured.Label);
                b.CloseElement();
            }
            b.CloseElement();
        }

        if (!string.IsNullOrWhiteSpace(notice))
        {
            b.OpenElement(i++, "pre");
            b.AddAttribute(i++, "class", "su-notice");
            b.AddContent(i++, notice);
            b.CloseElement();
        }

        if (fields.Count > 8)
            BuildSearch(ctx, b, ref i, key, query);

        List<Field> visible = Filter(fields, query);
        if (visible.Count == 0)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "su-empty");
            b.AddContent(i++, "没有匹配「" + query + "」的配置项");
            b.CloseElement();
            b.CloseElement();
            return;
        }

        foreach (IGrouping<string, Field> group in visible.GroupBy(f => f.Group))
        {
            string groupKey = key + "|" + group.Key;
            bool collapsed = Collapsed.TryGetValue(groupKey, out bool stored2) && stored2;
            BuildGroup(ctx, b, ref i, groupKey, group.Key, group.ToList(), collapsed, config);
        }

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-foot");
        b.AddContent(i++, "改完点下面的保存生效。说明里的数字和「实测」都是当初调出来的结论，改之前值得看一眼。");
        b.CloseElement();

        b.CloseElement();
    }

    // ── 反射：把 Config 的公开可写属性变成字段描述 ──────────────

    static List<Field> Describe(object config)
    {
        List<Field> list = new();
        foreach (PropertyInfo prop in config.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite)
                continue;

            string raw = prop.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? prop.Name;
            (string group, string label) = Split(raw);

            list.Add(new Field
            {
                Prop = prop,
                Group = group,
                Label = label,
                Hint = prop.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "",
                Order = prop.MetadataToken,   // 声明顺序，GetProperties 不保证
            });
        }
        return list.OrderBy(f => f.Order).ToList();
    }

    static (string Group, string Label) Split(string raw)
    {
        int dot = raw.IndexOf('·');
        if (dot <= 0 || dot >= raw.Length - 1)
            return ("常规", raw.Trim());
        return (raw[..dot].Trim(), raw[(dot + 1)..].Trim());
    }

    static List<Field> Filter(List<Field> fields, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return fields;

        string q = query.Trim();
        return fields.Where(f =>
            f.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.Group.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.Hint.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // ── 顶部 hero（与语音页同款）────────────────────────────────

    static void BuildHero(RenderTreeBuilder b, ref int i, string title, string subtitle, List<Field> fields)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-title");
        b.AddContent(i++, title);
        b.CloseElement();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-hero");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-hero-title");
        b.AddContent(i++, title);
        b.CloseElement();

        if (subtitle.Length > 0)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "su-hero-sub");
            b.AddContent(i++, subtitle);
            b.CloseElement();
        }

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-hero-chain");
        bool first = true;
        foreach (string group in fields.Select(f => f.Group).Distinct())
        {
            if (first == false)
            {
                b.OpenElement(i++, "span");
                b.AddAttribute(i++, "class", "su-arrow");
                b.AddContent(i++, "→");
                b.CloseElement();
            }
            first = false;

            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "su-node");
            b.AddContent(i++, group);
            b.CloseElement();
        }
        b.CloseElement();   // chain

        b.CloseElement();   // hero
    }

    static void BuildSearch(Ctx ctx, RenderTreeBuilder b, ref int i, string key, string query)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-search");

        b.OpenComponent<Input<string>>(i++);
        b.AddAttribute(i++, "Value", query);
        b.AddAttribute(i++, "Placeholder", "搜配置项（名称、分组或说明里的字）");
        b.AddAttribute(i++, "AllowClear", true);
        b.AddAttribute(i++, "ValueChanged", EventCallback.Factory.Create<string>(ctx.Host, value =>
        {
            Queries[key] = value ?? "";
            ctx.Refresh();
        }));
        b.CloseComponent();

        b.CloseElement();
    }

    // ── 分区面板（左侧色条，对齐 gs-panel）──────────────────────

    static void BuildGroup(Ctx ctx, RenderTreeBuilder b, ref int i, string groupKey, string name, List<Field> fields, bool collapsed, object config)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", collapsed ? "su-panel su-panel-collapsed" : "su-panel");

        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "su-panel-head");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(ctx.Host, _ =>
        {
            Collapsed[groupKey] = !collapsed;
            ctx.Refresh();
        }));

        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "su-panel-dot");
        b.CloseElement();

        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "su-panel-name");
        b.AddContent(i++, name);
        b.CloseElement();

        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "su-panel-count");
        b.AddContent(i++, fields.Count.ToString(CultureInfo.InvariantCulture));
        b.CloseElement();

        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "su-caret");
        b.AddContent(i++, collapsed ? "▸" : "▾");
        b.CloseElement();

        b.CloseElement();   // head

        if (collapsed == false)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "su-panel-body");
            foreach (Field field in fields)
                BuildRow(ctx, b, ref i, config, field);
            b.CloseElement();
        }

        b.CloseElement();   // panel
    }

    // ── 单行：标签 + 说明 + 控件 ───────────────────────────────

    static void BuildRow(Ctx ctx, RenderTreeBuilder b, ref int i, object config, Field field)
    {
        Type type = field.Prop.PropertyType;
        object? value = SafeGet(field.Prop, config);

        bool multiline = type == typeof(string)
            && field.Prop.GetCustomAttribute<UiTextAreaAttribute>() != null;

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", type == typeof(bool) ? "su-row su-row-bool"
            : multiline ? "su-row su-row-wide" : "su-row");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-row-text");
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-label");
        b.AddContent(i++, field.Label);
        b.CloseElement();
        if (field.Hint.Length > 0)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "su-hint");
            b.AddContent(i++, field.Hint);
            b.CloseElement();
        }
        b.CloseElement();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "su-ctl");

        if (type == typeof(bool))
        {
            bool current = value is bool flag && flag;
            b.OpenComponent<Switch>(i++);
            b.AddAttribute(i++, "Checked", current);
            b.AddAttribute(i++, "CheckedChildren", "开");
            b.AddAttribute(i++, "UnCheckedChildren", "关");
            b.AddAttribute(i++, "CheckedChanged", EventCallback.Factory.Create<bool>(ctx.Host, next =>
            {
                Commit(ctx, config, field.Prop, next);
            }));
            b.CloseComponent();
        }
        else if (type == typeof(int))
        {
            int current = value is int number ? number : 0;
            b.OpenComponent<InputNumber<int>>(i++);
            b.AddAttribute(i++, "Value", current);
            b.AddAttribute(i++, "Style", "width:100%;");
            b.AddAttribute(i++, "ValueChanged", EventCallback.Factory.Create<int>(ctx.Host, next =>
            {
                Commit(ctx, config, field.Prop, next);
            }));
            b.CloseComponent();
        }
        else if (type == typeof(double) || type == typeof(float))
        {
            double current = value is double d ? d : value is float f ? f : 0;
            b.OpenComponent<InputNumber<double>>(i++);
            b.AddAttribute(i++, "Value", current);
            b.AddAttribute(i++, "Step", 0.1);
            b.AddAttribute(i++, "Style", "width:100%;");
            b.AddAttribute(i++, "ValueChanged", EventCallback.Factory.Create<double>(ctx.Host, next =>
            {
                Commit(ctx, config, field.Prop, (float)next);
            }));
            b.CloseComponent();
        }
        else if (type == typeof(string))
        {
            string text = value as string ?? "";
            if (multiline)
            {
                // 提示词、文案这类长文本：多行框，整行铺开，别塞进 220px 的窄栏
                b.OpenElement(i++, "textarea");
                b.AddAttribute(i++, "class", "su-textarea");
                b.AddAttribute(i++, "rows", "10");
                b.AddAttribute(i++, "value", text);
                b.AddAttribute(i++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(ctx.Host,
                    (ChangeEventArgs e) => Commit(ctx, config, field.Prop, e.Value?.ToString() ?? "")));
                b.AddContent(i++, text);
                b.CloseElement();
            }
            else
            {
                b.OpenComponent<Input<string>>(i++);
                b.AddAttribute(i++, "Value", text);
                b.AddAttribute(i++, "ValueChanged", EventCallback.Factory.Create<string>(ctx.Host, next =>
                {
                    Commit(ctx, config, field.Prop, next ?? "");
                }));
                b.CloseComponent();
            }
        }
        else
        {
            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "su-unsupported");
            b.AddContent(i++, value?.ToString() ?? "（不支持的类型）");
            b.CloseElement();
        }

        // 插件追加的按钮（比如「查看 / 填入内置提示词」）：放在控件正下方，跟配置项同格
        ctx.RowTail?.Invoke(b, i, field.Prop.Name);

        b.CloseElement();   // ctl
        b.CloseElement();   // row
    }

    static object? SafeGet(PropertyInfo prop, object config)
    {
        try { return prop.GetValue(config); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// 写回配置。失败不抛——一个字段写不进去不该让整个设置页白掉，
    /// 但也不能假装成功，所以错误吞掉之后框架保存时值仍是旧的，用户会看见。
    /// </summary>
    static void Commit(Ctx ctx, object config, PropertyInfo prop, object value)
    {
        try
        {
            if (prop.PropertyType == typeof(float) && value is double d)
                value = (float)d;
            prop.SetValue(config, value);
        }
        catch (Exception)
        {
            // 类型不匹配（比如把空串写给 int）就保持旧值
        }
        ctx.Refresh();
    }

    // ── 样式（gs- 皮肤）──────────────────────────────────────────

    static void InjectStyles(RenderTreeBuilder b, ref int i)
    {
        b.OpenElement(i++, "style");
        b.AddContent(i++, """
            .su-root{
                --su-accent:#eb2f96;
                --su-pink:#eb2f96;
                --su-pink-soft:#f759ab;
                --su-border:#f7e0ec;
                --su-panel:#fffafd;
                --su-text:#434343;
                --su-sub:#8c8c8c;
                max-width:820px;padding:18px 20px 22px;border-radius:16px;
                background:linear-gradient(180deg,#fff0f7 0%,#ffffff 140px);
                color:var(--su-text);font-size:13px;line-height:1.6;
                border:1px solid #fce4f0;
            }
            @media (prefers-color-scheme:dark){
                .su-root{background:linear-gradient(180deg,#2a1622 0%,#16181d 140px);border-color:#3a2532;color:#c9cdd4;}
                .su-panel{background:#1e2127 !important;border-color:#33232c !important;box-shadow:none !important;}
                .su-panel-head,.su-label{color:#c9cdd4;}
                .su-hint,.su-sub,.su-foot,.su-panel-count{color:#8a9099;}
                .su-hero{background:linear-gradient(120deg,#5c2340 0%,#401a2e 100%) !important;}
                .su-act{border-color:#d36ba6 !important;color:#f0b6d3 !important;}
                .su-notice{background:#1e2127 !important;border-color:#33232c !important;color:#c9cdd4;}
            }
            .su-title{font-size:13px;font-weight:600;color:var(--su-sub);margin-bottom:8px;letter-spacing:.4px;}
            .su-hero{
                position:relative;overflow:hidden;border-radius:12px;padding:14px 16px;margin-bottom:16px;
                background:linear-gradient(120deg,#eb2f96 0%,#f759ab 60%,#ff9ad5 100%);
                color:#fff;box-shadow:0 4px 14px rgba(235,47,150,0.20);
            }
            .su-hero::before{
                content:"";position:absolute;right:-30px;top:-40px;width:140px;height:140px;border-radius:50%;
                background:rgba(255,255,255,0.10);
            }
            .su-hero-title{font-size:15px;font-weight:700;position:relative;z-index:1;}
            .su-hero-sub{font-size:12px;opacity:.92;margin-top:4px;line-height:1.6;position:relative;z-index:1;}
            .su-hero-chain{display:flex;align-items:center;gap:6px;flex-wrap:wrap;margin-top:10px;position:relative;z-index:1;}
            .su-node{
                font-size:11px;padding:2px 9px;border-radius:999px;
                background:rgba(255,255,255,0.16);border:1px solid rgba(255,255,255,0.28);
            }
            .su-arrow{font-size:11px;opacity:.7;}

            .su-search{margin-bottom:12px;}
            .su-actions{display:flex;gap:8px;flex-wrap:wrap;margin:0 0 12px;}
            .su-act{
                font-size:12px;padding:4px 14px;border-radius:999px;cursor:pointer;font-family:inherit;font-weight:600;
                border:1px solid var(--su-pink);background:transparent;color:var(--su-pink);
                transition:background .15s ease,color .15s ease;
            }
            .su-act:hover{background:linear-gradient(120deg,var(--su-pink) 0%,var(--su-pink-soft) 100%);color:#fff;border-color:transparent;}
            .su-notice{
                margin:0 0 12px;padding:10px 12px;border-radius:10px;max-height:260px;overflow:auto;
                white-space:pre-wrap;word-break:break-word;font-size:11.5px;line-height:1.7;
                background:#fff;border:1px solid var(--su-border);
                font-family:ui-monospace,Consolas,"Cascadia Mono",monospace;
            }

            .su-panel{
                position:relative;margin-bottom:16px;padding:14px 16px 8px 18px;
                border:1px solid var(--su-border);border-radius:10px;background:var(--su-panel);
                box-shadow:0 2px 8px rgba(114,46,209,0.05);
            }
            .su-panel::before{
                content:"";position:absolute;left:0;top:0;bottom:0;width:4px;
                background:linear-gradient(180deg,var(--su-pink-soft),var(--su-pink));
                border-radius:10px 0 0 10px;
            }
            .su-panel-collapsed{padding-bottom:14px;}
            .su-panel-head{
                display:flex;align-items:center;gap:8px;width:100%;padding:0;margin:0 0 10px;
                border:0;background:transparent;color:var(--su-text);font:inherit;font-size:14px;font-weight:600;
                cursor:pointer;text-align:left;
            }
            .su-panel-collapsed .su-panel-head{margin-bottom:0;}
            .su-panel-dot{
                width:8px;height:8px;border-radius:50%;background:var(--su-pink);
                box-shadow:0 0 0 3px rgba(235,47,150,0.14);flex:0 0 auto;
            }
            .su-panel-name{flex:1 1 auto;}
            .su-panel-count{
                font-size:11px;font-weight:500;color:var(--su-sub);padding:1px 7px;border-radius:999px;
                background:#fff;border:1px solid var(--su-border);
            }
            .su-caret{font-size:10px;color:var(--su-pink);opacity:.85;}
            .su-panel-body{padding-top:2px;}

            .su-row{
                display:grid;grid-template-columns:minmax(0,1fr) 220px;gap:14px;
                align-items:start;padding:9px 0;border-bottom:1px dashed #f0ebf7;
            }
            .su-row:last-child{border-bottom:0;}
            .su-row-bool{grid-template-columns:minmax(0,1fr) auto;align-items:center;}
            .su-row-wide{grid-template-columns:minmax(0,1fr);gap:6px;}
            .su-row-wide .su-ctl{flex-direction:column;align-items:stretch;gap:8px;}
            .su-row-text{min-width:0;}
            .su-label{font-weight:600;font-size:12.5px;color:var(--su-text);}
            .su-row-bool .su-label{font-weight:500;}
            .su-hint{
                font-size:11px;color:var(--su-sub);margin-top:2px;line-height:1.55;
                display:-webkit-box;-webkit-line-clamp:3;-webkit-box-orient:vertical;overflow:hidden;
            }
            .su-row:hover .su-hint{-webkit-line-clamp:unset;}
            .su-ctl{display:flex;align-items:center;justify-content:flex-end;}
            .su-ctl>*{width:100%;}
            .su-row-bool .su-ctl>*{width:auto;}
            .su-textarea{
                width:100%;box-sizing:border-box;resize:vertical;min-height:150px;
                font-family:inherit;font-size:12px;line-height:1.65;padding:8px 10px;border-radius:10px;
                border:1px solid #f7d6e6;background:linear-gradient(180deg,#fff 0%,#fffafd 100%);
                color:inherit;outline:none;
            }
            .su-textarea:focus{border-color:#f759ab;box-shadow:0 0 0 3px rgba(247,89,171,0.18);}
            .su-root .ant-input,.su-root .ant-input-number{
                width:100%;border-radius:10px !important;border:1px solid #f7d6e6 !important;
                background:linear-gradient(180deg,#fff 0%,#fffafd 100%) !important;
                box-shadow:0 1px 4px rgba(235,47,150,0.06),inset 0 1px 0 rgba(255,255,255,0.8) !important;
            }
            .su-root .ant-input:hover,.su-root .ant-input-number:hover{border-color:#ffadd2 !important;}
            .su-root .ant-input:focus,.su-root .ant-input-number-focused{
                border-color:#f759ab !important;box-shadow:0 0 0 3px rgba(247,89,171,0.18) !important;
            }
            .su-empty{padding:26px 0;text-align:center;font-size:12px;color:var(--su-sub);}
            .su-foot{margin-top:10px;font-size:11px;color:var(--su-sub);}
            .su-unsupported{font-size:11px;color:var(--su-sub);}
            """);
        b.CloseElement();
    }
}

/// <summary>标在字符串配置项上：用多行文本框渲染（提示词、文案这类长文本）。</summary>
public sealed class UiTextAreaAttribute : Attribute
{
}
