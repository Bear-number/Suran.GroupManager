using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace Suran.GroupManager;

/// <summary>
/// 通用配置界面，直接采用 BreezeTTS 的 br-* 组件结构与样式：
/// br-page / br-panel / br-panel-header / br-field-grid / br-field / br-input /
/// br-toggle / br-textarea / br-note / br-code / br-pills / br-button。
///
/// 样式取自 Breeze 资产（与 QChat / EmotionTTS / RVC 同源），只保留设置页用得到的组件；
/// 颜色令牌不在这里定义，嵌在 Breeze 页面里就继承 --br-*，独立设置页则用粉色回退值。
/// 所以不再有自定义的 su-* 皮肤，也就不会出现「只换了颜色、结构不是 Breeze」的问题。
///
/// 每个插件只需要在 XxxUI 里调一次 Render。
/// </summary>
public static class SuranUiKit
{
    /// <summary>折叠状态按「配置类型 + 分组」记，默认全展开</summary>
    static readonly Dictionary<string, bool> Collapsed = new(StringComparer.Ordinal);
    static readonly Dictionary<string, string> Queries = new(StringComparer.Ordinal);

    /// <summary>渲染上下文：ComponentBase.StateHasChanged 是 protected，只能由调用方传刷新委托</summary>
    sealed class Ctx
    {
        public ComponentBase Host = null!;
        public Action Refresh = static () => { };
        public Action<RenderTreeBuilder, int, string>? RowTail;
    }

    /// <summary>头部动作按钮。给需要"点一下跑个探测"的插件用</summary>
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
        string title, string subtitle,
        IReadOnlyList<UiAction>? actions = null, string? notice = null,
        Action<RenderTreeBuilder, int, string>? rowTail = null)
    {
        Ctx ctx = new() { Host = host, Refresh = refresh, RowTail = rowTail };
        int i = 0;
        b.OpenElement(i++, "section");
        b.AddAttribute(i++, "class", "br-page br-compact");
        b.AddAttribute(i++, "data-su-theme", ReadTheme(config));
        b.AddAttribute(i++, "style",
            "height:auto;min-height:0;max-width:100%;overflow:visible;padding:0;"
            + "display:grid;gap:16px;align-content:start;");

        InjectStyles(b, ref i);

        string key = config.GetType().FullName ?? "config";
        List<Field> fields = Describe(config);
        string query = Queries.TryGetValue(key, out string? stored) ? stored : "";

        BuildHeader(ctx, b, ref i, title, subtitle, fields, config);

        if (actions is { Count: > 0 })
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "br-card-actions");
            b.AddAttribute(i++, "style", "margin:0;padding:0;border:0;");
            foreach (UiAction action in actions)
            {
                UiAction captured = action;
                b.OpenElement(i++, "button");
                b.AddAttribute(i++, "type", "button");
                b.AddAttribute(i++, "class", "br-button br-small br-primary");
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
            b.AddAttribute(i++, "class", "br-code");
            b.AddContent(i++, notice);
            b.CloseElement();
        }

        if (fields.Count > 8)
            BuildSearch(ctx, b, ref i, key, query);

        List<Field> visible = Filter(fields, query);
        if (visible.Count == 0)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "br-empty");
            b.OpenElement(i++, "strong");
            b.AddContent(i++, "没有匹配「" + query + "」的配置项");
            b.CloseElement();
            b.OpenElement(i++, "span");
            b.AddContent(i++, "换个关键词试试，分组名和说明文字也会一起搜。");
            b.CloseElement();
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

        b.OpenElement(i++, "p");
        b.AddAttribute(i++, "class", "br-note");
        b.AddAttribute(i++, "style", "opacity:.8;");
        b.AddContent(i++, "改完点下面的保存生效。说明里的数字都是按实际运行调好的，改之前值得看一眼。");
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

            // 主题由页头的切换按钮管，不当作普通配置项渲染
            if (prop.Name == "UiTheme")
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

    // ── 顶部信息卡（br-panel + br-kicker + br-pills）────────────

    static void BuildHeader(Ctx ctx, RenderTreeBuilder b, ref int i, string title, string subtitle, List<Field> fields, object config)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "br-panel");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "br-panel-header");
        b.OpenElement(i++, "div");

        b.OpenElement(i++, "p");
        b.AddAttribute(i++, "class", "br-kicker");
        b.AddContent(i++, "SURAN · PLUGIN");
        b.CloseElement();

        b.OpenElement(i++, "h2");
        b.AddAttribute(i++, "class", "br-panel-title");
        b.AddContent(i++, title);
        b.CloseElement();

        b.CloseElement();

        // 单独的粉 / 黑切换：写进本插件的 UiTheme，刷新后整页换色
        bool dark = ReadTheme(config) == "dark";
        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "br-button br-small br-ghost");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(ctx.Host, _ =>
        {
            WriteTheme(config, dark ? "pink" : "dark");
            ctx.Refresh();
        }));
        b.AddContent(i++, dark ? "◐ 粉色主题" : "◑ 黑夜主题");
        b.CloseElement();

        b.CloseElement();

        if (subtitle.Length > 0)
        {
            b.OpenElement(i++, "p");
            b.AddAttribute(i++, "class", "br-note");
            b.AddContent(i++, subtitle);
            b.CloseElement();
        }

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "br-pills");
        b.AddAttribute(i++, "style", "margin-top:12px;");
        foreach (string group in fields.Select(f => f.Group).Distinct())
        {
            b.OpenElement(i++, "span");
            b.AddContent(i++, group);
            b.CloseElement();
        }
        b.CloseElement();

        b.CloseElement();
    }

    static string ReadTheme(object config)
    {
        try
        {
            object? value = config.GetType()
                .GetProperty("UiTheme", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(config);
            return string.Equals(value as string, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "pink";
        }
        catch (Exception)
        {
            return "pink";
        }
    }

    static void WriteTheme(object config, string theme)
    {
        try
        {
            config.GetType()
                .GetProperty("UiTheme", BindingFlags.Public | BindingFlags.Instance)?
                .SetValue(config, theme);
        }
        catch (Exception)
        {
            // 主题只是外观，写不进去不影响配置本身
        }
    }

    static void BuildSearch(Ctx ctx, RenderTreeBuilder b, ref int i, string key, string query)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "br-panel");
        b.AddAttribute(i++, "style", "padding:14px 16px;");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "br-field");
        b.AddAttribute(i++, "style", "max-width:420px;");

        b.OpenElement(i++, "span");
        b.AddContent(i++, "搜索配置");
        b.CloseElement();

        b.OpenElement(i++, "input");
        b.AddAttribute(i++, "class", "br-input");
        b.AddAttribute(i++, "type", "text");
        b.AddAttribute(i++, "placeholder", "搜配置项（名称、分组或说明里的字）");
        b.AddAttribute(i++, "value", query);
        b.AddAttribute(i++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(ctx.Host,
            (ChangeEventArgs e) =>
            {
                Queries[key] = e.Value?.ToString() ?? "";
                ctx.Refresh();
            }));
        b.CloseElement();

        b.CloseElement();
        b.CloseElement();
    }

    // ── 分组（details.br-panel + br-field-grid）─────────────────

    static void BuildGroup(Ctx ctx, RenderTreeBuilder b, ref int i, string groupKey, string name, List<Field> fields, bool collapsed, object config)
    {
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "br-panel br-advanced");

        b.OpenElement(i++, "button");
        b.AddAttribute(i++, "type", "button");
        b.AddAttribute(i++, "class", "br-panel-header");
        b.AddAttribute(i++, "style", "width:100%;text-align:left;");
        b.AddAttribute(i++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(ctx.Host, _ =>
        {
            Collapsed[groupKey] = !collapsed;
            ctx.Refresh();
        }));

        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "class", "br-panel-title");
        b.AddContent(i++, name);
        b.CloseElement();

        b.OpenElement(i++, "small");
        b.AddAttribute(i++, "style",
            "color:var(--br-dim,#a08494);font-family:var(--br-mono,Consolas,monospace);"
            + "font-size:9px;letter-spacing:.12em;margin-left:auto;padding-right:10px;");
        b.AddContent(i++, fields.Count.ToString(CultureInfo.InvariantCulture) + " 项");
        b.CloseElement();

        b.OpenElement(i++, "span");
        b.AddAttribute(i++, "style", "color:var(--br-dim,#a08494);font-size:10px;");
        b.AddContent(i++, collapsed ? "▸" : "▾");
        b.CloseElement();

        b.CloseElement();   // header

        if (collapsed == false)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "br-field-grid");
            b.AddAttribute(i++, "style", "margin-top:16px;");
            foreach (Field field in fields)
                BuildRow(ctx, b, ref i, config, field);
            b.CloseElement();
        }

        b.CloseElement();   // panel
    }

    // ── 单行：br-field（标签 + 控件 + 说明）────────────────────

    static void BuildRow(Ctx ctx, RenderTreeBuilder b, ref int i, object config, Field field)
    {
        Type type = field.Prop.PropertyType;
        object? value = SafeGet(field.Prop, config);

        bool multiline = type == typeof(string)
            && field.Prop.GetCustomAttribute<UiTextAreaAttribute>() != null;

        bool wide = type == typeof(bool) || multiline;

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "br-field");
        if (wide)
            b.AddAttribute(i++, "style", "grid-column:1 / -1;");

        if (type == typeof(bool))
        {
            bool current = value is bool flag && flag;

            b.OpenElement(i++, "label");
            b.AddAttribute(i++, "class", "br-toggle");

            b.OpenElement(i++, "input");
            b.AddAttribute(i++, "type", "checkbox");
            b.AddAttribute(i++, "checked", current);
            b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(ctx.Host,
                (ChangeEventArgs e) => Commit(ctx, config, field.Prop, e.Value is bool next && next)));
            b.CloseElement();

            b.OpenElement(i++, "span");
            b.AddContent(i++, field.Label);
            b.CloseElement();

            b.CloseElement();   // label
        }
        else
        {
            b.OpenElement(i++, "span");
            b.AddContent(i++, field.Label);
            b.CloseElement();

            if (type == typeof(int))
            {
                int current = value is int number ? number : 0;
                b.OpenElement(i++, "input");
                b.AddAttribute(i++, "class", "br-input");
                b.AddAttribute(i++, "type", "number");
                b.AddAttribute(i++, "value", current.ToString(CultureInfo.InvariantCulture));
                b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(ctx.Host,
                    (ChangeEventArgs e) =>
                    {
                        if (int.TryParse(e.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int next))
                            Commit(ctx, config, field.Prop, next);
                    }));
                b.CloseElement();
            }
            else if (type == typeof(double) || type == typeof(float))
            {
                double current = value is double d ? d : value is float f ? f : 0;
                b.OpenElement(i++, "input");
                b.AddAttribute(i++, "class", "br-input");
                b.AddAttribute(i++, "type", "number");
                b.AddAttribute(i++, "step", "0.1");
                b.AddAttribute(i++, "value", current.ToString(CultureInfo.InvariantCulture));
                b.AddAttribute(i++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(ctx.Host,
                    (ChangeEventArgs e) =>
                    {
                        if (double.TryParse(e.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double next))
                            Commit(ctx, config, field.Prop, (float)next);
                    }));
                b.CloseElement();
            }
            else if (type == typeof(string))
            {
                string text = value as string ?? "";
                if (multiline)
                {
                    b.OpenElement(i++, "textarea");
                    b.AddAttribute(i++, "class", "br-textarea");
                    b.AddAttribute(i++, "rows", "8");
                    b.AddAttribute(i++, "value", text);
                    b.AddAttribute(i++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(ctx.Host,
                        (ChangeEventArgs e) => Commit(ctx, config, field.Prop, e.Value?.ToString() ?? "")));
                    b.AddContent(i++, text);
                    b.CloseElement();
                }
                else
                {
                    b.OpenElement(i++, "input");
                    b.AddAttribute(i++, "class", "br-input");
                    b.AddAttribute(i++, "type", "text");
                    b.AddAttribute(i++, "value", text);
                    b.AddAttribute(i++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(ctx.Host,
                        (ChangeEventArgs e) => Commit(ctx, config, field.Prop, e.Value?.ToString() ?? "")));
                    b.CloseElement();
                }
            }
            else
            {
                b.OpenElement(i++, "span");
                b.AddContent(i++, value?.ToString() ?? "（不支持的类型）");
                b.CloseElement();
            }
        }

        if (field.Hint.Length > 0)
        {
            b.OpenElement(i++, "small");
            b.AddContent(i++, field.Hint);
            b.CloseElement();
        }

        // 插件追加的按钮（比如「查看 / 填入内置提示词」）：跟配置项同格
        ctx.RowTail?.Invoke(b, i, field.Prop.Name);

        b.CloseElement();   // br-field
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

    // ── 样式：Breeze 资产里设置页用到的 br-* 规则 + 少量补充 ─────

    static void InjectStyles(RenderTreeBuilder b, ref int i)
    {
        b.OpenElement(i++, "style");
        b.AddContent(i++, Css);
        b.CloseElement();

        b.OpenElement(i++, "style");
        b.AddContent(i++, """
            .br-page .br-field-grid > .br-note,
            .br-page .br-field-grid > .br-code,
            .br-page .br-field-grid > .br-card-actions{grid-column:1 / -1;}
            .br-page .br-field > small{color:var(--br-muted,#7c6272);font-size:11px;line-height:1.6;}
            .br-page .br-panel > .br-field-grid{margin-top:16px;}
            @media (max-width:760px){
                .br-page .br-field-grid{grid-template-columns:1fr;}
            }

            /* 插件页默认粉色：不依赖外层是否定义了令牌，嵌在别的皮肤里也保持自己的粉 */
            .br-page[data-su-theme="pink"]{
                --br-bg:#fff1f7;--br-bg-2:#fff6fa;
                --br-surface:#fffafd;--br-surface-2:#ffedf6;--br-surface-3:#ffe1ee;--br-field:#fff6fb;
                --br-ink:#3b2533;--br-muted:#7c6272;--br-dim:#a08494;
                --br-line:#f2cfe0;--br-line-soft:#fbe2ee;
                --br-accent:#e0448d;--br-accent-rgb:224,68,141;--br-accent-ink:#fff6fb;
                --br-secondary:#8c5cb8;--br-secondary-rgb:140,92,184;
                --br-warn:#b26a3d;--br-danger:#cf3a63;
                --br-glass:rgba(255,247,252,.94);--br-shadow:0 24px 60px rgba(196,64,132,.14);
                --br-sun:#ffadd1;--br-sun-ink:#4a2438;
                --br-lilac:#f7e3f3;--br-mint:#e6f0ea;
                --br-paper:#fff9fc;--br-paper-edge:#f1d2e2;--br-petal:#f0b6d3;
                --br-hero-bg:#ffeaf3;--br-hero-ink:#3a2130;--br-hero-wash:255,234,243;
                --br-scheme:light;
            }

            /* 插件自己的粉/黑开关；框架切黑夜时也强制黑夜（html[data-theme="dark"]）
               这里用 data-su-theme，避免嵌在 QChat 的 .br-page[data-theme] 里时误命中宿主的主题规则 */
            .br-page[data-su-theme="dark"],
            html[data-theme="dark"] .br-page{
                --br-bg:#151016;--br-bg-2:#1b1420;
                --br-surface:#211a26;--br-surface-2:#2a2131;--br-surface-3:#33283b;--br-field:#1c1621;
                --br-ink:#f4e9f1;--br-muted:#b9a7b7;--br-dim:#8e7b8d;
                --br-line:#3c2f43;--br-line-soft:#2f2536;
                --br-accent:#ff7ab8;--br-accent-rgb:255,122,184;--br-accent-ink:#2b1220;
                --br-secondary:#c6a2e6;--br-secondary-rgb:198,162,230;
                --br-warn:#f0b46a;--br-danger:#ff6f92;
                --br-glass:rgba(31,24,36,.92);--br-shadow:0 24px 60px rgba(0,0,0,.5);
                --br-sun:#ffb3d3;--br-sun-ink:#2b1220;
                --br-lilac:#2c2433;--br-mint:#22302a;
                --br-paper:#251d2a;--br-paper-edge:#3c2f43;--br-petal:#4b2f44;
                --br-hero-bg:#241a2a;--br-hero-ink:#f7ecf3;--br-hero-wash:30,22,36;
                --br-scheme:dark;
            }

            /* 嵌在 Breeze 页面（QChat 扩展页）里时不再画一层整页底色，颜色交给宿主卡片 */
            .br-page .br-page{
                background:transparent;border:0;border-radius:0;box-shadow:none;
                min-height:0;height:auto;overflow:visible;
            }
            """);
        b.CloseElement();
    }

    const string Css = """
.br-page {
  
  

  
  --br-radius: 21px;

  --br-sans: "Segoe UI Variable", "Segoe UI", "Microsoft YaHei UI", "PingFang SC", "Hiragino Sans GB", sans-serif;
  --br-serif: "Iowan Old Style", "Palatino Linotype", "Noto Serif CJK SC", "Songti SC", "SimSun", serif;
  --br-display: "Segoe UI Variable Display", "Segoe UI", "Arial Black", "Helvetica Neue", "PingFang SC", "Microsoft YaHei UI", sans-serif;
  --br-mono: "Cascadia Code", "SFMono-Regular", Consolas, "Liberation Mono", monospace;
  --br-ease: cubic-bezier(.22, 1, .36, 1);
  --br-quick: cubic-bezier(.2, 1, .3, 1);

  
  --br-intro-exit-at: 2300;
  --br-intro-exit-ms: 820;
  --br-intro-skip-ms: 180;

  position: relative;
  isolation: isolate;
  display: flex;
  flex-direction: column;
  width: 100%;
  height: 100%;
  min-height: 100%;
  overflow-x: hidden;
  overflow-y: auto;
  overscroll-behavior: contain;
  scrollbar-gutter: stable;
  color: var(--br-ink, #3b2533);
  background:
    radial-gradient(ellipse at 10% 8%, rgba(var(--br-accent-rgb, 224, 68, 141), .10), transparent 36%),
    radial-gradient(ellipse at 93% 2%, rgba(var(--br-secondary-rgb, 140, 92, 184), .09), transparent 42%),
    var(--br-bg, #fff1f7);
  color-scheme: var(--br-scheme, light);
  font-family: var(--br-sans, "Segoe UI", "Microsoft YaHei UI", "PingFang SC", sans-serif);
  font-size: 14px;
  line-height: 1.6;
  -webkit-font-smoothing: antialiased;
  -webkit-tap-highlight-color: transparent;
  scrollbar-width: thin;
  scrollbar-color: var(--br-line, #f2cfe0) transparent;
}

.br-page,
.br-page *,
.br-page *::before,
.br-page *::after { box-sizing: border-box; }

.br-page :where(button, input, textarea, select) { font: inherit; color: inherit; }

.br-page :where(button, select, summary) { cursor: pointer; }

.br-page :where(button) { border: 0; background: none; padding: 0; }

.br-page :where(button:disabled) { cursor: not-allowed; opacity: .55; }

.br-page :where(a) { color: var(--br-accent, #e0448d); text-decoration-thickness: 1px; text-underline-offset: 2px; }

.br-page :where(img, svg, video) { display: block; max-width: 100%; }

.br-page :where(img) { height: auto; }

.br-page :where(h1, h2, h3, h4, h5, h6) { margin: 0; line-height: 1.3; }

.br-page :where(p) { margin: 0; }

.br-page :where(small) { font-size: 11px; }

.br-page :where(hr) { border: 0; border-top: 1px dashed var(--br-line, #f2cfe0); margin: 18px 0; }

.br-page :where(ul, ol) { margin: 0; padding-left: 1.25em; }

.br-page :where([hidden]) { display: none !important; }

.br-page :where(:focus-visible) { outline: 2px solid var(--br-accent, #e0448d); outline-offset: 3px; }

.br-page ::selection { background: rgba(var(--br-accent-rgb, 224, 68, 141), .22); color: var(--br-ink, #3b2533); }

.br-page::-webkit-scrollbar { width: 10px; height: 10px; }

.br-page::-webkit-scrollbar-track { background: transparent; }

.br-page::-webkit-scrollbar-thumb {
  background: var(--br-line, #f2cfe0);
  border: 3px solid var(--br-bg, #fff1f7);
  border-radius: 999px;
}

.br-page .br-badge {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-height: 24px;
  padding: 4px 10px;
  border: 1px solid var(--br-line, #f2cfe0);
  border-radius: 999px;
  background: var(--br-field, #fff6fb);
  color: var(--br-muted, #7c6272);
  font-family: var(--br-mono, "Cascadia Code", "SFMono-Regular", Consolas, monospace);
  font-size: 9px;
  letter-spacing: .1em;
  line-height: 1;
  text-transform: uppercase;
  white-space: nowrap;
}

.br-page .br-badge::before {
  content: "";
  width: 5px;
  height: 5px;
  flex: 0 0 auto;
  border-radius: 50%;
  background: currentColor;
  opacity: .7;
}

.br-page .br-badge.br-ok { color: var(--br-accent, #e0448d); background: var(--br-mint, #e6f0ea); border-color: transparent; }

.br-page .br-badge.br-warn { color: var(--br-warn, #b26a3d); background: var(--br-paper, #fff9fc); border-color: var(--br-paper-edge, #f1d2e2); }

.br-page .br-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: clamp(14px, 2vw, 20px);
  align-items: start;
}

.br-page .br-grid > button.br-panel {
  text-align: left;
  cursor: pointer;
  transition: border-color .24s ease, transform .3s var(--br-ease, cubic-bezier(.22, 1, .36, 1));
}

.br-page .br-grid > button.br-panel:hover { border-color: var(--br-accent, #e0448d); transform: translateY(-2px); }

.br-page .br-grid > button.br-panel h3 {
  margin: 0 0 5px;
  font-family: var(--br-serif, "Iowan Old Style", "Palatino Linotype", "Noto Serif CJK SC", "Songti SC", "SimSun", serif);
  font-size: 18px;
  font-weight: 500;
  letter-spacing: -.02em;
}

.br-page .br-grid > button.br-panel p { color: var(--br-muted, #7c6272); font-size: 11px; line-height: 1.7; }

.br-page .br-grid > button.br-panel strong {
  display: block;
  margin-top: 10px;
  color: var(--br-accent, #e0448d);
  font-size: 11px;
  font-weight: 500;
}

.br-page .br-panel {
  position: relative;
  min-width: 0;
  padding: clamp(18px, 2vw, 24px);
  border: 1px solid var(--br-line-soft, #fbe2ee);
  border-radius: var(--br-radius, 21px);
  background: var(--br-surface, #fffafd);
}

.br-page .br-panel::before {
  content: "";
  position: absolute;
  inset: 0;
  border-radius: inherit;
  background: linear-gradient(180deg, rgba(255, 255, 255, .30), transparent 34%);
  pointer-events: none;
  opacity: .55;
}

.br-page .br-panel > * { position: relative; z-index: 1; }

.br-page .br-panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 16px;
}

.br-page .br-panel-title {
  display: flex;
  align-items: center;
  gap: 9px;
  min-width: 0;
  margin: 0;
  font-size: 15px;
  font-weight: 600;
  letter-spacing: -.01em;
}

.br-page .br-panel-title::before {
  content: "";
  width: 7px;
  height: 7px;
  flex: 0 0 auto;
  background: var(--br-accent, #e0448d);
  clip-path: polygon(50% 0, 58% 42%, 100% 50%, 58% 58%, 50% 100%, 42% 58%, 0 50%, 42% 42%);
}

.br-page .br-panel-title small {
  color: var(--br-dim, #a08494);
  font-family: var(--br-mono, "Cascadia Code", "SFMono-Regular", Consolas, monospace);
  font-size: 9px;
  font-weight: 400;
  letter-spacing: .12em;
  text-transform: uppercase;
}

.br-page .br-main > .br-panel-header {
  position: relative;
  flex-wrap: wrap;
  margin-bottom: 2px;
  padding: 2px 2px 0;
}

.br-page .br-main > .br-panel-header > div { min-width: 0; }

.br-page .br-main > .br-panel-header .br-panel-title {
  margin: 2px 0 0;
  font-family: var(--br-serif, "Iowan Old Style", "Palatino Linotype", "Noto Serif CJK SC", "Songti SC", "SimSun", serif);
  font-size: clamp(24px, 2.6vw, 32px);
  font-weight: 500;
  letter-spacing: -.025em;
  line-height: 1.2;
}

.br-page .br-main > .br-panel-header > .br-button { margin-left: auto; }

.br-page .br-button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  min-height: 40px;
  padding: 10px 18px;
  border: 1px solid var(--br-line, #f2cfe0);
  border-radius: 999px;
  background: var(--br-surface-2, #ffedf6);
  color: var(--br-ink, #3b2533);
  font-family: var(--br-sans, "Segoe UI", "Microsoft YaHei UI", "PingFang SC", sans-serif);
  font-size: 12px;
  font-weight: 500;
  letter-spacing: .01em;
  line-height: 1.2;
  text-decoration: none;
  white-space: nowrap;
  transition: background .24s ease, border-color .24s ease, color .24s ease,
              transform .24s var(--br-ease, cubic-bezier(.22, 1, .36, 1)), box-shadow .24s ease;
}

.br-page .br-button:hover:not(:disabled) { transform: translateY(-2px); }

.br-page .br-button:active:not(:disabled) { transform: translateY(0); }

.br-page .br-button.br-primary {
  background: var(--br-sun, #ffadd1);
  border-color: var(--br-sun, #ffadd1);
  color: var(--br-sun-ink, #4a2438);
  box-shadow: 0 4px 0 rgba(130, 70, 52, .08);
}

.br-page .br-button.br-primary:hover:not(:disabled) {
  transform: translateY(-3px) rotate(-1deg);
  box-shadow: 0 8px 18px rgba(var(--br-accent-rgb, 224, 68, 141), .18);
}

.br-page .br-button.br-small { min-height: 32px; padding: 6px 13px; font-size: 11px; gap: 6px; }

.br-page .br-button.br-ghost { background: transparent; border-color: transparent; color: var(--br-muted, #7c6272); }

.br-page .br-button.br-ghost:hover:not(:disabled) { background: var(--br-surface-2, #ffedf6); color: var(--br-ink, #3b2533); }

.br-page .br-button.br-danger {
  background: transparent;
  border-color: var(--br-danger, #cf3a63);
  color: var(--br-danger, #cf3a63);
}

.br-page .br-button.br-danger:hover:not(:disabled) { background: rgba(var(--br-accent-rgb, 224, 68, 141), .07); }

.br-page .br-button :where(svg, img) { width: 15px; height: 15px; flex: 0 0 auto; }

.br-page .br-field {
  display: flex;
  flex-direction: column;
  gap: 7px;
  min-width: 0;
}

.br-page .br-field > :where(label, span, small) {
  color: var(--br-muted, #7c6272);
  font-size: 11px;
  line-height: 1.5;
}

.br-page .br-field > :where(label, strong) { color: var(--br-ink, #3b2533); font-weight: 500; }

.br-page .br-field > :where(label, strong) small { margin-left: 5px; color: var(--br-dim, #a08494); font-weight: 400; }

.br-page .br-field-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 14px;
}

.br-page .br-input,
.br-page .br-textarea,
.br-page .br-select {
  width: 100%;
  min-height: 40px;
  padding: 9px 13px;
  border: 1px solid var(--br-line, #f2cfe0);
  border-radius: 12px;
  background: var(--br-field, #fff6fb);
  color: var(--br-ink, #3b2533);
  font-family: var(--br-sans, "Segoe UI", "Microsoft YaHei UI", "PingFang SC", sans-serif);
  font-size: 12px;
  line-height: 1.5;
  outline: none;
  transition: border-color .2s ease, box-shadow .2s ease, background .2s ease;
}

.br-page .br-input::placeholder,
.br-page .br-textarea::placeholder { color: var(--br-dim, #a08494); opacity: .9; }

.br-page .br-input:hover,
.br-page .br-textarea:hover,
.br-page .br-select:hover { border-color: var(--br-paper-edge, #f1d2e2); }

.br-page .br-input:focus,
.br-page .br-textarea:focus,
.br-page .br-select:focus {
  border-color: var(--br-accent, #e0448d);
  box-shadow: 0 0 0 3px rgba(var(--br-accent-rgb, 224, 68, 141), .13);
  background: var(--br-surface, #fffafd);
}

.br-page .br-input:disabled,
.br-page .br-textarea:disabled,
.br-page .br-select:disabled { opacity: .6; cursor: not-allowed; }

.br-page .br-textarea { min-height: 104px; resize: vertical; line-height: 1.75; }

.br-page .br-select {
  appearance: none;
  -webkit-appearance: none;
  padding-right: 34px;
  background-image:
    linear-gradient(45deg, transparent 50%, var(--br-muted, #7c6272) 50%),
    linear-gradient(135deg, var(--br-muted, #7c6272) 50%, transparent 50%);
  background-position:
    calc(100% - 17px) calc(50% - 2px),
    calc(100% - 12px) calc(50% - 2px);
  background-size: 5px 5px, 5px 5px;
  background-repeat: no-repeat;
}

.br-page .br-toggle {
  display: inline-flex;
  align-items: center;
  gap: 10px;
  min-height: 30px;
  color: var(--br-muted, #7c6272);
  font-size: 12px;
  cursor: pointer;
  user-select: none;
}

.br-page .br-toggle input {
  appearance: none;
  -webkit-appearance: none;
  position: relative;
  flex: 0 0 auto;
  width: 38px;
  height: 22px;
  margin: 0;
  border: 1px solid var(--br-line, #f2cfe0);
  border-radius: 999px;
  background: var(--br-surface-3, #ffe1ee);
  cursor: pointer;
  transition: background .24s ease, border-color .24s ease;
}

.br-page .br-toggle input::after {
  content: "";
  position: absolute;
  top: 2px;
  left: 2px;
  width: 16px;
  height: 16px;
  border-radius: 50%;
  background: var(--br-surface, #fffafd);
  box-shadow: 0 1px 3px rgba(49, 34, 51, .22);
  transition: transform .24s var(--br-ease, cubic-bezier(.22, 1, .36, 1));
}

.br-page .br-toggle input:checked {
  background: var(--br-accent, #e0448d);
  border-color: var(--br-accent, #e0448d);
}

.br-page .br-toggle input:checked::after { transform: translateX(16px); }

.br-page .br-toggle input:disabled { opacity: .5; cursor: not-allowed; }

.br-page .br-toggle:has(input:disabled) { opacity: .6; cursor: not-allowed; }

.br-page .br-pills {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 7px;
}

.br-page .br-pills > button,
.br-page .br-pills > span {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  min-height: 28px;
  padding: 4px 11px;
  border: 1px solid var(--br-line, #f2cfe0);
  border-radius: 999px;
  background: transparent;
  color: var(--br-muted, #7c6272);
  font-size: 10px;
  line-height: 1.2;
  white-space: nowrap;
}

.br-page .br-pills > button:hover { border-color: var(--br-accent, #e0448d); color: var(--br-ink, #3b2533); }

.br-page .br-pills > button[aria-pressed="true"],
.br-page .br-pills > button[aria-selected="true"],
.br-page .br-pills > .active {
  color: var(--br-accent, #e0448d);
  background: rgba(var(--br-accent-rgb, 224, 68, 141), .09);
  border-color: rgba(var(--br-accent-rgb, 224, 68, 141), .32);
}

.br-page .br-note {
  margin: 0;
  color: var(--br-muted, #7c6272);
  font-size: 11px;
  line-height: 1.8;
}

.br-page .br-note strong { color: var(--br-ink, #3b2533); font-weight: 500; }

.br-page .br-empty {
  display: grid;
  place-items: center;
  gap: 9px;
  min-height: 150px;
  padding: 30px 22px;
  border: 1px dashed var(--br-line, #f2cfe0);
  border-radius: 18px;
  background: rgba(var(--br-accent-rgb, 224, 68, 141), .025);
  color: var(--br-muted, #7c6272);
  font-size: 12px;
  line-height: 1.8;
  text-align: center;
}

.br-page .br-empty strong { color: var(--br-ink, #3b2533); font-weight: 500; }

.br-page .br-empty .br-button { margin-top: 4px; }

.br-page .br-empty h2 {
  margin: 0;
  font-family: var(--br-serif, "Iowan Old Style", "Palatino Linotype", "Noto Serif CJK SC", "Songti SC", "SimSun", serif);
  font-size: clamp(19px, 2vw, 23px);
  font-weight: 500;
  letter-spacing: -.02em;
}

.br-page .br-empty p { max-width: 420px; }

.br-page .br-empty > span:first-child { color: var(--br-accent, #e0448d); font-size: 20px; }

.br-page .br-panel.br-field-grid > .br-card-actions { grid-column: 1 / -1; }

.br-page .br-panel.br-field-grid > .br-card-actions > .br-button:first-child { margin-left: 0; }

.br-page .br-code {
  max-width: 100%;
  margin: 0;
  padding: 11px 13px;
  overflow: auto;
  border: 1px solid var(--br-line, #f2cfe0);
  border-radius: 12px;
  background: var(--br-field, #fff6fb);
  color: var(--br-ink, #3b2533);
  font-family: var(--br-mono, "Cascadia Code", "SFMono-Regular", Consolas, monospace);
  font-size: 11px;
  line-height: 1.75;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.br-page .br-card-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: auto;
  padding: 10px 16px;
  border-top: 1px dashed var(--br-line, #f2cfe0);
}

.br-page .br-card-actions .br-note { margin-right: auto; }

.br-page .br-savebar {
  position: sticky;
  bottom: 14px;
  z-index: 30;
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 11px;
  width: min(1358px, calc(100% - 24px));
  margin: 4px auto 16px;
  padding: 10px 13px 10px 22px;
  border: 1px solid var(--br-line, #f2cfe0);
  border-radius: 999px;
  background: var(--br-glass, rgba(255,247,252,.94));
  box-shadow: 0 8px 30px rgba(75, 45, 47, .10);
  -webkit-backdrop-filter: blur(18px);
  backdrop-filter: blur(18px);
}

.br-page .br-savebar > .br-note { margin-right: auto; font-size: 10px; }

.br-page .br-savebar > div:first-child {
  display: flex;
  flex: 1 1 220px;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.br-page .br-savebar > div:first-child strong { font-size: 11px; font-weight: 600; line-height: 1.4; }

.br-page .br-savebar > div:first-child small { color: var(--br-dim, #a08494); font-size: 9px; line-height: 1.5; }

.br-page .br-savebar > div:last-child {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  margin-left: auto;
}

.br-page .br-savebar > div:last-child .br-button { min-height: 34px; padding: 7px 14px; font-size: 11px; }

.br-page .br-savebar > .br-note::before {
  content: "";
  display: inline-block;
  width: 6px;
  height: 6px;
  margin-right: 8px;
  border-radius: 50%;
  background: var(--br-accent, #e0448d);
  vertical-align: .08em;
}

.br-page.br-compact .br-kicker { margin-bottom: 12px; }

.br-page.br-compact .br-panel { padding: 18px 20px; }
""";
}

/// <summary>标在字符串配置项上：用多行文本框渲染（提示词、文案这类长文本）。</summary>
public sealed class UiTextAreaAttribute : Attribute
{
}
