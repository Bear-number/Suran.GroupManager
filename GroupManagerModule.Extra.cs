// 群资料、禁言名单、管理员设置、批量群管、入群欢迎增强、反刷屏、消息缓存撤回、成员变动与操作日志查询。
// 传输与权限全部复用 GroupManagerModule.cs 里的 CallActionAsync / ParseActionResponse / GateOperator / LogOperation，
// 这里只写新动作与本地记录，不另起连接。
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace Suran.GroupManager;

public partial class GroupManagerModule
{
    // ---- 反刷屏 / 消息缓存参数 ----
    const int SpamWindowSeconds = 10;
    const int SpamActionCooldownSeconds = 60;
    const int SpamObserveCooldownSeconds = 30;
    const int MaxSpamWindows = 5000;
    const int RecentMessagePerGroup = 50;
    const int MaxCachedGroups = 200;

    // ---- 本地文件 ----
    const string MemberChangeFile = "member-changes.jsonl";
    const string OperationLogFile = "operation-log.jsonl";

    static readonly object storageLock = new();
    static readonly object messageCacheLock = new();
    static readonly ConcurrentDictionary<long, Queue<CachedMessage>> messageCache = new();
    static readonly ConcurrentDictionary<string, SpamWindow> spamWindows = new();
    static long cachedSelfId;
    static bool selfIdResolved;

    sealed record CachedMessage(string MessageId, long UserId, long Time);

    sealed record MutedMember(long UserId, string Nickname, long RemainingSeconds);

    sealed class SpamWindow
    {
        public readonly object Gate = new();
        public readonly Queue<(ulong Hash, DateTime At)> Recent = new();
        public DateTime LastActionAt = DateTime.MinValue;
        public DateTime LastNotifiedAt = DateTime.MinValue;
    }

    // ============================================================
    // 本地存储：插件目录下的 jsonl
    // ============================================================

    string PluginStorageDirectory()
    {
        string directory = Path.Combine(AlifePath.StorageFolderPath, Character.StorageKey, "GroupManager");
        Directory.CreateDirectory(directory);
        return directory;
    }

    void AppendJsonLine(string fileName, string json)
    {
        string path = Path.Combine(PluginStorageDirectory(), fileName);
        lock (storageLock)
        {
            File.AppendAllText(path, json + Environment.NewLine, new UTF8Encoding(false));
        }
    }

    List<string> ReadJsonlLines(string fileName)
    {
        try
        {
            string path = Path.Combine(PluginStorageDirectory(), fileName);
            if (File.Exists(path) == false)
            {
                return new List<string>();
            }
            return File.ReadAllLines(path, Encoding.UTF8)
                .Where(line => line.Trim().Length > 0)
                .ToList();
        }
        catch (Exception readError)
        {
            logger.LogWarning("[群管] 读取本地记录失败（{File}）：{Message}", fileName, readError.Message);
            return new List<string>();
        }
    }

    static DateTime ParseLogTime(string text)
    {
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed
            : DateTime.MinValue;
    }

    void AppendMemberChange(long groupId, long userId, string nickname, string changeType, long operatorId)
    {
        if (Configuration.MemberChangeLog == false)
        {
            return;
        }
        try
        {
            JsonObject entry = new()
            {
                ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["group_id"] = groupId,
                ["user_id"] = userId,
                ["nickname"] = nickname,
                ["type"] = changeType
            };
            if (operatorId > 0)
            {
                entry["operator_id"] = operatorId;
            }
            AppendJsonLine(MemberChangeFile, entry.ToJsonString());
        }
        catch (Exception logError)
        {
            logger.LogWarning("[群管] 写成员变动记录失败：{Message}", logError.Message);
        }
    }

    // ============================================================
    // 入群欢迎增强
    // ============================================================

    // 配置格式「群号=模板」，逐行匹配；没有任何一行命中或整段解析失败都退回全局模板
    string ResolveWelcomeTemplate(long groupId)
    {
        string global = Configuration.WelcomeTemplate ?? "";
        if (global.Trim().Length == 0)
        {
            global = "欢迎 {nickname} 加入本群～";
        }
        string raw = Configuration.WelcomeGroupTemplates ?? "";
        foreach (string line in raw.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }
            int separator = trimmed.IndexOfAny(new[] { '=', '＝' });
            if (separator <= 0)
            {
                continue;
            }
            string groupPart = trimmed[..separator].Trim();
            string templatePart = trimmed[(separator + 1)..].Trim();
            if (templatePart.Length == 0)
            {
                continue;
            }
            if (long.TryParse(groupPart, out long configuredGroup) && configuredGroup == groupId)
            {
                return templatePart;
            }
        }
        return global;
    }

    // @ 段用 at + text 两段拼，部分协议端不支持 at 时会退化成纯文本，不影响发送
    async Task SendGroupWelcomeAsync(long groupId, long userId, string text, bool atNewcomer)
    {
        JsonArray messageArray = new();
        if (atNewcomer)
        {
            messageArray.Add(new JsonObject
            {
                ["type"] = "at",
                ["data"] = new JsonObject { ["qq"] = userId }
            });
            messageArray.Add(new JsonObject
            {
                ["type"] = "text",
                ["data"] = new JsonObject { ["text"] = " " + text }
            });
        }
        else
        {
            messageArray.Add(new JsonObject
            {
                ["type"] = "text",
                ["data"] = new JsonObject { ["text"] = text }
            });
        }
        JsonObject parameters = new()
        {
            ["group_id"] = groupId,
            ["message"] = messageArray
        };
        string response = await CallActionAsync("send_group_msg", parameters);
        ParseActionResponse(response, "发送群消息");
    }

    // ============================================================
    // 群消息事件：消息元数据缓存 + 反刷屏（默认关闭，默认只观察）
    // ============================================================

    async Task HandleGroupMessageEventAsync(JsonElement message)
    {
        try
        {
            long groupId = GetNumericField(message, "group_id");
            long userId = GetNumericField(message, "user_id");
            if (groupId == 0 || userId == 0)
            {
                return;
            }
            string messageType = GetStringField(message, "message_type");
            if (messageType.Length > 0 && messageType != "group")
            {
                return;
            }

            string messageId = GetMessageId(message);
            if (messageId.Length > 0)
            {
                CacheRecentMessage(groupId, messageId, userId, GetNumericField(message, "time"));
            }

            if (Configuration.AntiSpamEnabled == false || ShouldSkipAntiSpamGroup(groupId))
            {
                return;
            }
            long selfId = await GetSelfIdAsync();
            if (selfId > 0 && userId == selfId)
            {
                // 不处理自己发的消息，避免自动禁言/撤回 Bot 自己
                return;
            }

            string text = ExtractMessagePlainText(message).Trim();
            if (text.Length == 0)
            {
                return;
            }
            int textLength = text.Length;

            List<string> keywords = ParseKeywordList(Configuration.AntiSpamKeywords);
            string hitType = "";
            if (keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                hitType = "关键词";
            }
            else if (Configuration.AntiSpamBlockLinks && LooksLikeLink(text))
            {
                hitType = "链接";
            }

            int threshold = Configuration.AntiSpamRepeatThreshold;
            (int repeatCount, bool actionCooling, bool observeCooling, SpamWindow window, DateTime now) =
                RegisterSpamMessage(groupId, userId, Fnv1a(text));
            if (hitType.Length == 0 && threshold > 0 && repeatCount >= threshold)
            {
                hitType = "重复刷屏";
            }
            if (hitType.Length == 0)
            {
                return;
            }

            string action = NormalizeSpamAction();
            if (action == "observe")
            {
                if (observeCooling)
                {
                    return;
                }
                lock (window.Gate)
                {
                    window.LastNotifiedAt = now;
                }
                // 只记命中类型与正文长度，正文不落日志
                logger.LogInformation("[群管] 反刷屏观察：群 {Group} 用户 {User} 命中 {Type}（正文长度 {Length}），未执行操作",
                    groupId, userId, hitType, textLength);
                return;
            }
            if (actionCooling)
            {
                return;
            }

            try
            {
                if (action == "recall")
                {
                    if (messageId.Length == 0)
                    {
                        logger.LogWarning("[群管] 反刷屏撤回跳过：协议端未返回 message_id（群 {Group} 用户 {User}）", groupId, userId);
                        return;
                    }
                    JsonObject parameters = new() { ["message_id"] = messageId };
                    string response = await CallActionAsync("delete_msg", parameters);
                    ParseActionResponse(response, "撤回消息");
                    LogOperation("反刷屏撤回", "system", userId.ToString(CultureInfo.InvariantCulture),
                        "命中" + hitType, groupId);
                }
                else
                {
                    int muteSeconds = Math.Clamp(Configuration.AntiSpamMuteSeconds, 60, 2592000);
                    JsonObject parameters = new()
                    {
                        ["group_id"] = groupId,
                        ["user_id"] = userId,
                        ["duration"] = muteSeconds
                    };
                    string response = await CallActionAsync("set_group_ban", parameters);
                    ParseActionResponse(response, "禁言");
                    LogOperation("反刷屏禁言", "system", userId.ToString(CultureInfo.InvariantCulture),
                        "命中" + hitType + "，" + muteSeconds + "秒", groupId);
                }
            }
            catch (Exception actionError)
            {
                logger.LogWarning("[群管] 反刷屏执行失败（群 {Group} 用户 {User}）：{Message}",
                    groupId, userId, actionError.Message);
            }
            finally
            {
                lock (window.Gate)
                {
                    window.LastActionAt = now;
                    window.LastNotifiedAt = now;
                }
            }
        }
        catch (Exception messageError)
        {
            logger.LogWarning("[群管] 处理群消息事件异常：{Message}", messageError.Message);
        }
    }

    static string GetMessageId(JsonElement message)
    {
        string messageId = GetStringField(message, "message_id");
        if (messageId.Length == 0)
        {
            messageId = GetStringField(message, "message_seq");
        }
        return messageId;
    }

    static string ExtractMessagePlainText(JsonElement message)
    {
        if (message.TryGetProperty("message", out JsonElement body) == false)
        {
            // 部分协议端只给 raw_message
            return GetStringField(message, "raw_message");
        }
        if (body.ValueKind == JsonValueKind.Array)
        {
            return ExtractSegmentText(body);
        }
        if (body.ValueKind == JsonValueKind.String)
        {
            return body.GetString() ?? "";
        }
        return "";
    }

    // 只保存元数据（message_id / user_id / time），不留正文
    static void CacheRecentMessage(long groupId, string messageId, long userId, long time)
    {
        lock (messageCacheLock)
        {
            if (messageCache.TryGetValue(groupId, out Queue<CachedMessage> queue) == false)
            {
                if (messageCache.Count >= MaxCachedGroups)
                {
                    messageCache.Clear();
                }
                queue = new Queue<CachedMessage>();
                messageCache[groupId] = queue;
            }
            queue.Enqueue(new CachedMessage(messageId, userId, time));
            while (queue.Count > RecentMessagePerGroup)
            {
                queue.Dequeue();
            }
        }
    }

    static List<CachedMessage> SnapshotRecentMessages(long groupId)
    {
        lock (messageCacheLock)
        {
            return messageCache.TryGetValue(groupId, out Queue<CachedMessage> queue)
                ? queue.ToList()
                : new List<CachedMessage>();
        }
    }

    bool ShouldSkipAntiSpamGroup(long groupId)
    {
        List<long> configuredGroups = ParseIdList(Configuration.AntiSpamGroups);
        return configuredGroups.Count > 0 && configuredGroups.Contains(groupId) == false;
    }

    string NormalizeSpamAction()
    {
        string action = (Configuration.AntiSpamAction ?? "").Trim().ToLowerInvariant();
        return action is "recall" or "mute" ? action : "observe";
    }

    // 滑动窗口只存内容哈希与时间；返回命中次数与两种冷却状态
    static (int RepeatCount, bool ActionCooling, bool ObserveCooling, SpamWindow Window, DateTime Now)
        RegisterSpamMessage(long groupId, long userId, ulong contentHash)
    {
        if (spamWindows.Count > MaxSpamWindows)
        {
            spamWindows.Clear();
        }
        string key = groupId.ToString(CultureInfo.InvariantCulture) + ":" + userId.ToString(CultureInfo.InvariantCulture);
        SpamWindow window = spamWindows.GetOrAdd(key, _ => new SpamWindow());
        DateTime now = DateTime.UtcNow;
        lock (window.Gate)
        {
            while (window.Recent.Count > 0 && (now - window.Recent.Peek().At).TotalSeconds > SpamWindowSeconds)
            {
                window.Recent.Dequeue();
            }
            window.Recent.Enqueue((contentHash, now));
            while (window.Recent.Count > 60)
            {
                window.Recent.Dequeue();
            }
            int repeatCount = window.Recent.Count(item => item.Hash == contentHash);
            bool actionCooling = window.LastActionAt != DateTime.MinValue
                && (now - window.LastActionAt).TotalSeconds < SpamActionCooldownSeconds;
            bool observeCooling = window.LastNotifiedAt != DateTime.MinValue
                && (now - window.LastNotifiedAt).TotalSeconds < SpamObserveCooldownSeconds;
            return (repeatCount, actionCooling, observeCooling, window, now);
        }
    }

    static ulong Fnv1a(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char character in text)
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    static bool LooksLikeLink(string text)
    {
        return text.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || text.Contains("https://", StringComparison.OrdinalIgnoreCase)
            || text.Contains("www.", StringComparison.OrdinalIgnoreCase)
            || text.Contains("t.cn/", StringComparison.OrdinalIgnoreCase)
            || text.Contains("mqqapi://", StringComparison.OrdinalIgnoreCase);
    }

    async Task<long> GetSelfIdAsync()
    {
        if (selfIdResolved)
        {
            return cachedSelfId;
        }
        selfIdResolved = true;
        try
        {
            string response = await CallActionAsync("get_login_info", new JsonObject());
            JsonElement data = ParseActionResponse(response, "获取登录信息");
            cachedSelfId = GetNumericField(data, "user_id");
        }
        catch (Exception loginError)
        {
            logger.LogInformation("[群管] 读取 Bot 账号失败，反刷屏不做自身消息过滤：{Message}", loginError.Message);
            cachedSelfId = 0;
        }
        return cachedSelfId;
    }

    // ============================================================
    // AI 函数：群资料与禁言名单
    // ============================================================

    [XmlFunction(FunctionMode.OneShot, "get_group_detail")]
    [Description("获取群资料：群名、群号、成员数/上限、群主、管理员列表（昵称+QQ）、是否全员禁言。协议端没返回的字段标注未知，不做猜测")]
    public async Task GetGroupDetail(
        [Description("群号")] long groupId,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        try
        {
            JsonObject infoParameters = new() { ["group_id"] = groupId };
            string infoResponse = await CallActionAsync("get_group_info", infoParameters);
            JsonElement info = ParseActionResponse(infoResponse, "获取群信息");

            string groupName = GetStringField(info, "group_name").Trim();
            long memberCount = GetNumericField(info, "member_count");
            long maxMemberCount = GetNumericField(info, "max_member_count");
            long ownerId = GetNumericField(info, "owner_id");
            bool? allMuted = TryGetBoolField(info, "group_all_shut");

            List<JsonElement> ownerMembers = new();
            List<JsonElement> adminMembers = new();
            bool memberListAvailable = true;
            int listedMembers = 0;
            try
            {
                JsonObject listParameters = new() { ["group_id"] = groupId };
                string listResponse = await CallActionAsync("get_group_member_list", listParameters);
                JsonElement members = ParseActionResponse(listResponse, "获取成员列表");
                foreach (JsonElement member in members.EnumerateArray())
                {
                    listedMembers++;
                    string role = GetStringField(member, "role");
                    if (role == "owner")
                    {
                        ownerMembers.Add(member.Clone());
                    }
                    else if (role == "admin")
                    {
                        adminMembers.Add(member.Clone());
                    }
                }
            }
            catch (Exception listError)
            {
                memberListAvailable = false;
                logger.LogInformation("[群管] 群 {Group} 成员列表获取失败，群资料里的管理员标注未知：{Message}",
                    groupId, listError.Message);
            }

            // 成员数两路取：群信息字段优先，缺失时按成员列表实数
            if (memberCount <= 0 && listedMembers > 0)
            {
                memberCount = listedMembers;
            }
            if (ownerId == 0 && ownerMembers.Count > 0)
            {
                ownerId = GetNumericField(ownerMembers[0], "user_id");
            }

            string ownerText = "未知";
            if (ownerId > 0)
            {
                JsonElement ownerMember = ownerMembers.FirstOrDefault(
                    member => GetNumericField(member, "user_id") == ownerId);
                if (ownerMember.ValueKind == JsonValueKind.Object)
                {
                    ownerText = FormatMemberBrief(ownerMember);
                }
                else
                {
                    string ownerNickname = await LookupNicknameAsync(groupId, ownerId);
                    ownerText = ownerNickname.Length > 0
                        ? ownerNickname + "(" + ownerId + ")"
                        : ownerId.ToString(CultureInfo.InvariantCulture);
                }
            }

            List<string> lines = new()
            {
                "📋 群资料：",
                "群名: " + (groupName.Length > 0 ? groupName : "未知"),
                "群号: " + groupId,
                "成员数: " + (memberCount > 0 ? memberCount.ToString(CultureInfo.InvariantCulture) : "未知")
                    + " / 上限: " + (maxMemberCount > 0 ? maxMemberCount.ToString(CultureInfo.InvariantCulture) : "未知"),
                "群主: " + ownerText,
                "全员禁言: " + (allMuted.HasValue ? (allMuted.Value ? "已开启" : "未开启") : "未知")
            };

            if (memberListAvailable == false)
            {
                lines.Add("管理员: 未知（成员列表获取失败，可能是权限或协议端限制）");
            }
            else if (adminMembers.Count == 0)
            {
                lines.Add("管理员: 无（或该群没有设置管理员）");
            }
            else
            {
                lines.Add("管理员（" + adminMembers.Count + " 人）：");
                foreach (JsonElement admin in adminMembers.Take(20))
                {
                    lines.Add("- " + FormatMemberBrief(admin));
                }
                if (adminMembers.Count > 20)
                {
                    lines.Add("... 等共 " + adminMembers.Count + " 人");
                }
            }

            LogOperation("获取群资料", operatorId ?? "system", groupId.ToString(CultureInfo.InvariantCulture), "成功", groupId);
            interactor.Poke(ClampText(string.Join("\n", lines), 1800));
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot, "list_muted_members")]
    [Description("查看当前禁言名单（QQ、昵称、剩余秒数）。优先用 NapCat 的 get_group_shut_list；协议端不支持时改用成员列表的禁言字段并说明")]
    public async Task ListMutedMembers(
        [Description("群号")] long groupId,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        try
        {
            (bool supported, List<MutedMember> mutedMembers, string note) = await FetchMutedMembersAsync(groupId);
            List<string> lines;
            if (mutedMembers.Count == 0)
            {
                lines = new List<string> { "📋 当前没有禁言中的成员" };
            }
            else
            {
                lines = new List<string>
                {
                    supported
                        ? "📋 当前禁言名单（共 " + mutedMembers.Count + " 人）："
                        : "📋 当前禁言名单（共 " + mutedMembers.Count + " 人，来自成员列表兜底）："
                };
                foreach (MutedMember member in mutedMembers.Take(30))
                {
                    lines.Add("- " + FormatMutedMember(member));
                }
                if (mutedMembers.Count > 30)
                {
                    lines.Add("... 等共 " + mutedMembers.Count + " 人");
                }
            }
            if (note.Length > 0)
            {
                lines.Add("说明：" + note);
                lines.Add("替代方案：用 QueryMemberList 查看成员的禁言字段，或直接 UnbanMember 解除单个成员");
            }
            LogOperation("查看禁言名单", operatorId ?? "system", groupId.ToString(CultureInfo.InvariantCulture),
                "成功, 共" + mutedMembers.Count + "人", groupId);
            interactor.Poke(ClampText(string.Join("\n", lines), 1800));
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    // 禁言名单双通道：NapCat 的 get_group_shut_list 不支持（1404 或报错）时退回成员列表的 shut_up_timestamp
    async Task<(bool Supported, List<MutedMember> Members, string Note)> FetchMutedMembersAsync(long groupId)
    {
        try
        {
            JsonObject parameters = new() { ["group_id"] = groupId };
            string response = await CallActionAsync("get_group_shut_list", parameters);
            JsonElement data = ParseActionResponse(response, "获取禁言名单");
            JsonElement listElement = data;
            if (data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("list", out JsonElement nestedList)
                && nestedList.ValueKind == JsonValueKind.Array)
            {
                listElement = nestedList;
            }
            if (listElement.ValueKind != JsonValueKind.Array)
            {
                // 结构不认识时不当作「没人被禁言」，交给下面的成员列表兜底
                throw new Exception("禁言名单返回结构不认识");
            }
            List<MutedMember> members = new();
            foreach (JsonElement item in listElement.EnumerateArray())
            {
                long userId = GetNumericField(item, "user_id");
                if (userId == 0)
                {
                    userId = GetNumericField(item, "uin");
                }
                if (userId == 0)
                {
                    continue;
                }
                string nickname = GetStringField(item, "nickname").Trim();
                if (nickname.Length == 0)
                {
                    nickname = GetStringField(item, "card").Trim();
                }
                members.Add(new MutedMember(userId, nickname, ResolveRemainingSeconds(item)));
            }
            return (true, members, "");
        }
        catch (Exception shutListError)
        {
            // 1404 与其他报错都走兜底，避免换协议端后整个工具不可用
            logger.LogInformation("[群管] get_group_shut_list 不可用，改用成员列表兜底：{Message}", shutListError.Message);
        }

        JsonObject listParameters = new() { ["group_id"] = groupId };
        string listResponse = await CallActionAsync("get_group_member_list", listParameters);
        JsonElement memberData = ParseActionResponse(listResponse, "获取成员列表");
        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        List<MutedMember> fallbackMembers = new();
        foreach (JsonElement member in memberData.EnumerateArray())
        {
            long shutUpUntil = GetNumericField(member, "shut_up_timestamp");
            if (shutUpUntil <= now)
            {
                continue;
            }
            long userId = GetNumericField(member, "user_id");
            if (userId == 0)
            {
                continue;
            }
            string nickname = GetStringField(member, "card").Trim();
            if (nickname.Length == 0)
            {
                nickname = GetStringField(member, "nickname").Trim();
            }
            fallbackMembers.Add(new MutedMember(userId, nickname, shutUpUntil - now));
        }
        return (false, fallbackMembers,
            "该协议端不支持 get_group_shut_list，已改用 get_group_member_list 的禁言字段统计；若协议端也不返回该字段，名单可能不全");
    }

    static long ResolveRemainingSeconds(JsonElement item)
    {
        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        foreach (string field in new[] { "shut_up_time", "shut_up_timestamp", "shut_up_end_time", "duration" })
        {
            long value = GetNumericField(item, field);
            if (value <= 0)
            {
                continue;
            }
            // 大于 10 亿的一律当绝对时间戳，否则当剩余秒数
            return value > 1000000000L ? Math.Max(0, value - now) : value;
        }
        return -1;
    }

    static string FormatMutedMember(MutedMember member)
    {
        string nickname = member.Nickname.Length > 0 ? member.Nickname : "未知昵称";
        string remaining = member.RemainingSeconds >= 0
            ? "剩余 " + member.RemainingSeconds + " 秒"
            : "剩余 未知";
        return nickname + "(" + member.UserId + ") · " + remaining;
    }

    // ============================================================
    // AI 函数：管理员与批量操作
    // ============================================================

    [XmlFunction(FunctionMode.OneShot, "set_group_admin")]
    [Description("设置或取消群管理员（set_group_admin）。Bot 必须是群主，仅是管理员会被协议端拒绝；协议端不支持时会说明原因")]
    public async Task SetGroupAdmin(
        [Description("群号")] long groupId,
        [Description("目标QQ号")] long userId,
        [Description("true=设为管理员，false=取消管理员，默认true")] bool enable = true,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        try
        {
            JsonObject parameters = new()
            {
                ["group_id"] = groupId,
                ["user_id"] = userId,
                ["enable"] = enable
            };
            string response = await CallActionAsync("set_group_admin", parameters);
            ParseActionResponse(response, "设置群管理员");
            string actionText = enable ? "设置" : "取消";
            LogOperation(actionText + "群管理员", operatorId ?? "system",
                userId.ToString(CultureInfo.InvariantCulture), "成功", groupId);
            interactor.Poke("✅ 已" + actionText + "用户 " + userId + " 的群管理员身份");
        }
        catch (UnsupportedActionException unsupportedError)
        {
            interactor.Poke("❌ " + unsupportedError.Message
                + "\n说明：该协议端未实现 set_group_admin，无法通过接口设置管理员，请临时改用手机/电脑端操作");
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message
                + "\n说明：设置/取消管理员要求 Bot 是群主，仅管理员身份会被拒绝；Bot 是群主仍失败时，多为协议端不支持或触发风控");
        }
    }

    [XmlFunction(FunctionMode.OneShot, "batch_ban_members")]
    [Description("批量禁言：userIds 用逗号分隔的QQ号，逐条报告成功/失败。沿用现有权限判定，默认每人禁言600秒")]
    public async Task BatchBanMembers(
        [Description("群号")] long groupId,
        [Description("要禁言的QQ号，英文或中文逗号分隔")] string userIds,
        [Description("每人禁言秒数，默认600")] int durationSeconds = 600,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        List<long> targets = ParseIdList(userIds);
        if (targets.Count == 0)
        {
            interactor.Poke("❌ 没有识别出有效QQ号，请用逗号分隔，例如 123456,234567");
            return;
        }
        if (targets.Count > 50)
        {
            interactor.Poke("❌ 一次最多批量禁言 50 人（本次识别到 " + targets.Count + " 个）");
            return;
        }
        int seconds = durationSeconds <= 0 ? 600 : durationSeconds;
        int successCount = 0;
        List<string> failures = new();
        foreach (long userId in targets)
        {
            try
            {
                JsonObject parameters = new()
                {
                    ["group_id"] = groupId,
                    ["user_id"] = userId,
                    ["duration"] = seconds
                };
                string response = await CallActionAsync("set_group_ban", parameters);
                ParseActionResponse(response, "禁言");
                successCount++;
            }
            catch (Exception itemError)
            {
                failures.Add(userId + "：" + itemError.Message);
            }
        }

        LogOperation("批量禁言", operatorId ?? "system", string.Join(",", targets),
            "成功" + successCount + "，失败" + failures.Count, groupId);
        List<string> lines = new()
        {
            "✅ 批量禁言完成：成功 " + successCount + " / 共 " + targets.Count + " 人，每人 "
                + seconds + " 秒（约 " + Math.Max(1, seconds / 60) + " 分钟）"
        };
        if (failures.Count > 0)
        {
            lines.Add("❌ 失败项：");
            foreach (string failure in failures.Take(5))
            {
                lines.Add("- " + failure);
            }
            if (failures.Count > 5)
            {
                lines.Add("... 等共 " + failures.Count + " 项失败");
            }
        }
        interactor.Poke(ClampText(string.Join("\n", lines), 1600));
    }

    [XmlFunction(FunctionMode.OneShot, "batch_kick_members")]
    [Description("【高危】批量踢人：userIds 用逗号分隔，需在插件配置中启用踢人功能；逐条报告成功/失败")]
    public async Task BatchKickMembers(
        [Description("群号")] long groupId,
        [Description("要踢出的QQ号，英文或中文逗号分隔")] string userIds,
        [Description("是否同时拒绝这些用户的加群申请，默认否")] bool rejectAddRequest = false,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (Gate(Configuration.EnableKick, "踢人"))
        {
            return;
        }
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        List<long> targets = ParseIdList(userIds);
        if (targets.Count == 0)
        {
            interactor.Poke("❌ 没有识别出有效QQ号，请用逗号分隔，例如 123456,234567");
            return;
        }
        if (targets.Count > 50)
        {
            interactor.Poke("❌ 一次最多批量踢出 50 人（本次识别到 " + targets.Count + " 个）");
            return;
        }
        int successCount = 0;
        List<string> failures = new();
        foreach (long userId in targets)
        {
            try
            {
                JsonObject parameters = new()
                {
                    ["group_id"] = groupId,
                    ["user_id"] = userId,
                    ["reject_add_request"] = rejectAddRequest
                };
                string response = await CallActionAsync("set_group_kick", parameters);
                ParseActionResponse(response, "踢出成员");
                successCount++;
            }
            catch (Exception itemError)
            {
                failures.Add(userId + "：" + itemError.Message);
            }
        }

        string rejectText = rejectAddRequest ? "，已同时拒绝加群申请" : "";
        LogOperation("批量踢出成员【高危】", operatorId ?? "system", string.Join(",", targets),
            "成功" + successCount + "，失败" + failures.Count, groupId);
        List<string> lines = new()
        {
            "✅ 批量踢出完成：成功 " + successCount + " / 共 " + targets.Count + " 人" + rejectText
        };
        if (failures.Count > 0)
        {
            lines.Add("❌ 失败项：");
            foreach (string failure in failures.Take(5))
            {
                lines.Add("- " + failure);
            }
            if (failures.Count > 5)
            {
                lines.Add("... 等共 " + failures.Count + " 项失败");
            }
        }
        interactor.Poke(ClampText(string.Join("\n", lines), 1600));
    }

    [XmlFunction(FunctionMode.OneShot, "unban_all_members")]
    [Description("解除本群全部禁言：先取禁言名单再逐个解除，报告成功数与失败项；取不到名单时说明原因")]
    public async Task UnbanAllMembers(
        [Description("群号")] long groupId,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        try
        {
            (bool _, List<MutedMember> mutedMembers, string note) = await FetchMutedMembersAsync(groupId);
            List<string> lines = new();
            if (note.Length > 0)
            {
                lines.Add("说明：" + note);
            }
            if (mutedMembers.Count == 0)
            {
                lines.Add("📋 当前没有需要解除禁言的成员");
                interactor.Poke(ClampText(string.Join("\n", lines), 1600));
                return;
            }

            int successCount = 0;
            List<string> failures = new();
            foreach (MutedMember member in mutedMembers)
            {
                try
                {
                    JsonObject parameters = new()
                    {
                        ["group_id"] = groupId,
                        ["user_id"] = member.UserId,
                        ["duration"] = 0
                    };
                    string response = await CallActionAsync("set_group_ban", parameters);
                    ParseActionResponse(response, "解除禁言");
                    successCount++;
                }
                catch (Exception itemError)
                {
                    failures.Add(member.UserId + "：" + itemError.Message);
                }
            }

            LogOperation("批量解除禁言", operatorId ?? "system", "",
                "成功" + successCount + "，失败" + failures.Count, groupId);
            lines.Add("✅ 全部解除禁言完成：成功 " + successCount + " / 共 " + mutedMembers.Count + " 人");
            if (failures.Count > 0)
            {
                lines.Add("❌ 失败项：");
                foreach (string failure in failures.Take(5))
                {
                    lines.Add("- " + failure);
                }
                if (failures.Count > 5)
                {
                    lines.Add("... 等共 " + failures.Count + " 项失败");
                }
            }
            interactor.Poke(ClampText(string.Join("\n", lines), 1600));
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ 解除全部禁言失败：" + operationError.Message
                + "\n可先用 ListMutedMembers 确认名单是否取得到，再逐个 UnbanMember");
        }
    }

    // ============================================================
    // AI 函数：群名、消息撤回
    // ============================================================

    [XmlFunction(FunctionMode.OneShot, "set_group_name")]
    [Description("修改群名（set_group_name）。需要 Bot 是群主或管理员；不做群头像")]
    public async Task SetGroupName(
        [Description("群号")] long groupId,
        [Description("新的群名，最多30字")] string name,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        string trimmedName = (name ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            interactor.Poke("❌ 群名不能为空");
            return;
        }
        if (trimmedName.Length > 30)
        {
            trimmedName = trimmedName[..30];
        }
        try
        {
            JsonObject parameters = new()
            {
                ["group_id"] = groupId,
                ["group_name"] = trimmedName
            };
            string response = await CallActionAsync("set_group_name", parameters);
            ParseActionResponse(response, "修改群名");
            groupNameCache[groupId] = trimmedName + "(" + groupId + ")";
            LogOperation("修改群名", operatorId ?? "system", "", "成功: " + trimmedName, groupId);
            interactor.Poke("✅ 已把群 " + groupId + " 的群名改为：" + trimmedName);
        }
        catch (UnsupportedActionException unsupportedError)
        {
            interactor.Poke("❌ " + unsupportedError.Message + "\n说明：该协议端未实现 set_group_name，请改用手机/电脑端修改");
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message
                + "\n说明：改群名需要 Bot 是群主或管理员；权限足够仍失败时，多为协议端不支持或 QQ 风控限频，稍后再试");
        }
    }

    [XmlFunction(FunctionMode.OneShot, "recall_user_recent_messages")]
    [Description("撤回指定用户最近 N 条消息（默认1）。只对插件运行期间收到的群消息有效，缓存里没有时说明原因")]
    public async Task RecallUserRecentMessages(
        [Description("群号")] long groupId,
        [Description("目标QQ号")] long userId,
        [Description("最多撤回最近几条，默认1，最多20")] int count = 1,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        List<CachedMessage> cachedMessages = SnapshotRecentMessages(groupId);
        if (cachedMessages.Count == 0)
        {
            interactor.Poke("📋 该群消息缓存为空，无法撤回。\n原因：缓存只在插件运行期间实时积累（按群保留最近 "
                + RecentMessagePerGroup + " 条元数据），重启或刚升级后为空；更早的历史消息本工具查不到");
            return;
        }
        int wanted = Math.Clamp(count, 1, 20);
        List<CachedMessage> targets = cachedMessages
            .Where(item => item.UserId == userId)
            .TakeLast(wanted)
            .Reverse()
            .ToList();
        if (targets.Count == 0)
        {
            interactor.Poke("📋 缓存里最近 " + cachedMessages.Count + " 条消息中没有用户 " + userId
                + " 的消息（可能更早，或该用户近期没在本群发言）");
            return;
        }

        int successCount = 0;
        List<string> failures = new();
        foreach (CachedMessage target in targets)
        {
            try
            {
                JsonObject parameters = new() { ["message_id"] = target.MessageId };
                string response = await CallActionAsync("delete_msg", parameters);
                ParseActionResponse(response, "撤回消息");
                successCount++;
            }
            catch (Exception itemError)
            {
                failures.Add("消息 " + target.MessageId + "：" + itemError.Message);
            }
        }

        LogOperation("撤回用户最近消息", operatorId ?? "system", userId.ToString(CultureInfo.InvariantCulture),
            "成功" + successCount + "，失败" + failures.Count, groupId);
        List<string> lines = new()
        {
            "✅ 撤回完成：用户 " + userId + " 成功 " + successCount + " / 共 " + targets.Count + " 条"
        };
        if (successCount < targets.Count)
        {
            lines.Add("⚠️ 未全部成功，常见原因：消息超过2分钟、已被撤回、或 Bot 没有撤回权限");
        }
        if (failures.Count > 0)
        {
            lines.Add("❌ 失败项：");
            foreach (string failure in failures.Take(5))
            {
                lines.Add("- " + failure);
            }
            if (failures.Count > 5)
            {
                lines.Add("... 等共 " + failures.Count + " 项失败");
            }
        }
        interactor.Poke(ClampText(string.Join("\n", lines), 1600));
    }

    // ============================================================
    // AI 函数：本地记录查询
    // ============================================================

    [XmlFunction(FunctionMode.OneShot, "query_member_changes")]
    [Description("查入群/退群/被踢记录（本地 jsonl，只写本机不依赖协议端）。groupId 留空查所有群，days 默认7，limit 默认30")]
    public Task QueryMemberChanges(
        [Description("群号，留空=所有群")] string groupId = "",
        [Description("查最近几天，默认7")] int days = 7,
        [Description("最多返回几条，默认30")] int limit = 30,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (GateOperator(operatorId ?? "system"))
        {
            return Task.CompletedTask;
        }
        long groupFilter = ParseQQNumber(groupId ?? "");
        int dayRange = Math.Clamp(days, 1, 365);
        int wanted = Math.Clamp(limit, 1, 200);
        DateTime cutoff = DateTime.Now.AddDays(-dayRange);

        List<(DateTime Time, long GroupId, long UserId, string Nickname, string Type, long OperatorId)> records = new();
        foreach (string line in ReadJsonlLines(MemberChangeFile))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                DateTime time = ParseLogTime(GetStringField(root, "time"));
                if (time == DateTime.MinValue || time < cutoff)
                {
                    continue;
                }
                long recordGroupId = GetNumericField(root, "group_id");
                if (groupFilter > 0 && recordGroupId != groupFilter)
                {
                    continue;
                }
                records.Add((time, recordGroupId, GetNumericField(root, "user_id"),
                    GetStringField(root, "nickname"), GetStringField(root, "type"),
                    GetNumericField(root, "operator_id")));
            }
            catch (JsonException)
            {
                // 半行/坏行直接跳过，不影响其余记录
            }
        }

        if (records.Count == 0)
        {
            interactor.Poke("📋 近 " + dayRange + " 天没有成员变动记录"
                + (groupFilter > 0 ? "（群 " + groupFilter + "）" : "")
                + "。\n可能原因：「成员变动·记录」开关关闭，或插件刚升级还没收到新事件");
            return Task.CompletedTask;
        }

        List<(DateTime Time, long GroupId, long UserId, string Nickname, string Type, long OperatorId)> sorted =
            records.OrderByDescending(item => item.Time).Take(wanted).ToList();
        List<string> lines = new()
        {
            "📋 成员变动（近 " + dayRange + " 天，共 " + records.Count + " 条，按时间倒序显示 "
                + sorted.Count + " 条）："
        };
        foreach (var item in sorted)
        {
            string nickname = item.Nickname.Trim();
            string who = nickname.Length > 0
                ? nickname + "(" + item.UserId + ")"
                : item.UserId.ToString(CultureInfo.InvariantCulture);
            string operatorText = item.OperatorId > 0
                ? "，操作者 " + item.OperatorId.ToString(CultureInfo.InvariantCulture)
                : "";
            lines.Add("- [" + item.Time.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + "] 群"
                + item.GroupId + " " + who + " " + MemberChangeLabel(item.Type) + operatorText);
        }
        LogOperation("查询成员变动", operatorId ?? "system", groupFilter > 0 ? groupFilter.ToString(CultureInfo.InvariantCulture) : "",
            "成功, 共" + records.Count + "条", groupFilter);
        interactor.Poke(ClampText(string.Join("\n", lines), 1800));
        return Task.CompletedTask;
    }

    [XmlFunction(FunctionMode.OneShot, "query_operation_log")]
    [Description("查群管操作日志（本地 jsonl，与运行日志同内容）。groupId 留空查所有群，days 默认7，keyword 匹配操作/操作者/目标/结果，limit 默认30")]
    public Task QueryOperationLog(
        [Description("群号，留空=所有群")] string groupId = "",
        [Description("查最近几天，默认7")] int days = 7,
        [Description("可选关键词")] string keyword = "",
        [Description("最多返回几条，默认30")] int limit = 30,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (GateOperator(operatorId ?? "system"))
        {
            return Task.CompletedTask;
        }
        long groupFilter = ParseQQNumber(groupId ?? "");
        int dayRange = Math.Clamp(days, 1, 365);
        int wanted = Math.Clamp(limit, 1, 200);
        DateTime cutoff = DateTime.Now.AddDays(-dayRange);
        string filterKeyword = (keyword ?? "").Trim();

        List<(DateTime Time, long GroupId, string Operation, string Operator, string Target, string Result)> records = new();
        foreach (string line in ReadJsonlLines(OperationLogFile))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                DateTime time = ParseLogTime(GetStringField(root, "time"));
                if (time == DateTime.MinValue || time < cutoff)
                {
                    continue;
                }
                long recordGroupId = GetNumericField(root, "group_id");
                if (groupFilter > 0 && recordGroupId != groupFilter)
                {
                    continue;
                }
                string operation = GetStringField(root, "operation");
                string operatorText = GetStringField(root, "operator");
                string targetText = GetStringField(root, "target");
                string resultText = GetStringField(root, "result");
                if (filterKeyword.Length > 0)
                {
                    bool matched = operation.Contains(filterKeyword, StringComparison.OrdinalIgnoreCase)
                        || operatorText.Contains(filterKeyword, StringComparison.OrdinalIgnoreCase)
                        || targetText.Contains(filterKeyword, StringComparison.OrdinalIgnoreCase)
                        || resultText.Contains(filterKeyword, StringComparison.OrdinalIgnoreCase);
                    if (matched == false)
                    {
                        continue;
                    }
                }
                records.Add((time, recordGroupId, operation, operatorText, targetText, resultText));
            }
            catch (JsonException)
            {
                // 坏行跳过
            }
        }

        if (records.Count == 0)
        {
            interactor.Poke("📋 近 " + dayRange + " 天没有匹配的操作日志"
                + (groupFilter > 0 ? "（群 " + groupFilter + "）" : "")
                + (filterKeyword.Length > 0 ? "（关键词「" + filterKeyword + "」）" : "")
                + "。\n提示：日志文件在插件目录 operation-log.jsonl，从本版本起新操作都会写入");
            return Task.CompletedTask;
        }

        List<(DateTime Time, long GroupId, string Operation, string Operator, string Target, string Result)> sorted =
            records.OrderByDescending(item => item.Time).Take(wanted).ToList();
        List<string> lines = new()
        {
            "📋 群管操作日志（近 " + dayRange + " 天，共 " + records.Count + " 条，按时间倒序显示 "
                + sorted.Count + " 条）："
        };
        foreach (var item in sorted)
        {
            string groupText = item.GroupId > 0 ? "群" + item.GroupId + " " : "";
            string targetText = item.Target.Length > 0 ? " 目标 " + Clip(item.Target, 50) : "";
            lines.Add("- [" + item.Time.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + "] "
                + groupText + item.Operation + " | 操作者 " + item.Operator + targetText
                + " | " + Clip(item.Result, 120));
        }
        interactor.Poke(ClampText(string.Join("\n", lines), 2000));
        return Task.CompletedTask;
    }

    // ============================================================
    // 小工具
    // ============================================================

    static List<long> ParseIdList(string raw)
    {
        return (raw ?? "")
            .Split(new[] { ',', '，', ';', '；', ' ', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => long.TryParse(item, out long parsed) ? parsed : 0)
            .Where(item => item > 0)
            .Distinct()
            .ToList();
    }

    static List<string> ParseKeywordList(string raw)
    {
        return (raw ?? "")
            .Split(new[] { ',', '，', ';', '；', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static bool? TryGetBoolField(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Object || element.TryGetProperty(fieldName, out JsonElement field) == false)
        {
            return null;
        }
        return field.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => field.TryGetInt64(out long numericValue) && numericValue != 0,
            JsonValueKind.String => field.GetString() is "true" or "1" or "是",
            _ => null
        };
    }

    static string MemberChangeLabel(string changeType)
    {
        return changeType switch
        {
            "join" => "入群",
            "leave" => "退群",
            "kick" => "被踢",
            _ => changeType.Length > 0 ? changeType : "未知"
        };
    }

    static string Clip(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    static string ClampText(string text, int maxLength)
    {
        return text.Length <= maxLength ? text : text[..maxLength] + "\n...（内容较长已截断）";
    }
}
