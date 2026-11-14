using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Alife.Function.FunctionCaller;
using Alife.Foundation;
using Alife.Framework;
using Microsoft.Extensions.Logging;

namespace Suran.GroupManager;

public partial class GroupManagerModule
{
    // AI 函数：核心群管
    // ============================================================

    [XmlFunction(FunctionMode.OneShot)]
    [Description("禁言指定群成员。groupId群号，userId要禁言的QQ号，durationSeconds禁言秒数默认600")]
    public async Task BanMember(
        [Description("群号")] long groupId,
        [Description("要禁言的QQ号")] long userId,
        [Description("禁言时长（秒），0为解除禁言，默认600")] int durationSeconds = 600,
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
                ["duration"] = durationSeconds
            };
            string response = await CallActionAsync("set_group_ban", parameters);
            ParseActionResponse(response, "禁言");
            LogOperation("禁言", operatorId ?? "system", userId.ToString(), "成功，时长" + durationSeconds / 60 + "分钟");
            interactor.Poke("✅ 已禁言用户 " + userId + "，时长 " + durationSeconds / 60 + " 分钟");
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message + "（若提示权限相关，请提示用户把Bot设为群管理员）");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("解除指定群成员的禁言")]
    public async Task UnbanMember(
        [Description("群号")] long groupId,
        [Description("要解除禁言的QQ号")] long userId,
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
                ["duration"] = 0
            };
            string response = await CallActionAsync("set_group_ban", parameters);
            ParseActionResponse(response, "解除禁言");
            LogOperation("解除禁言", operatorId ?? "system", userId.ToString(), "成功");
            interactor.Poke("✅ 已解除用户 " + userId + " 的禁言");
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("设置群成员的群名片，card传空字符串表示取消名片")]
    public async Task SetMemberCard(
        [Description("群号")] long groupId,
        [Description("目标QQ号")] long userId,
        [Description("新的群名片，空字符串取消名片")] string card,
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
                ["card"] = card
            };
            string response = await CallActionAsync("set_group_card", parameters);
            ParseActionResponse(response, "设置名片");
            string cardDisplay = card.Length > 0 ? card : "(取消名片)";
            LogOperation("设置名片", operatorId ?? "system", userId.ToString(), "成功: " + cardDisplay);
            interactor.Poke("✅ 已设置用户 " + userId + " 的群名片为: " + cardDisplay);
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("撤回指定消息")]
    public async Task RecallMessage(
        [Description("要撤回的消息ID")] string messageId,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        try
        {
            JsonObject parameters = new() { ["message_id"] = messageId };
            string response = await CallActionAsync("delete_msg", parameters);
            ParseActionResponse(response, "撤回消息");
            LogOperation("撤回消息", operatorId ?? "system", "", "成功, 消息ID: " + messageId);
            interactor.Poke("✅ 已撤回消息 (ID: " + messageId + ")");
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("获取群成员列表。keyword可选，同时匹配QQ号/QQ昵称/群名片/专属头衔任一字段，不传返回全量列表前10条")]
    public async Task QueryMemberList(
        [Description("群号")] long groupId,
        [Description("可选搜索关键词")] string keyword = null,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        try
        {
            JsonObject parameters = new() { ["group_id"] = groupId };
            string response = await CallActionAsync("get_group_member_list", parameters);
            JsonElement members = ParseActionResponse(response, "获取成员列表");
            string filteredKeyword = (keyword ?? "").Trim();
            List<JsonElement> matchedMembers = new();
            foreach (JsonElement member in members.EnumerateArray())
            {
                if (filteredKeyword.Length == 0 || MemberMatchesKeyword(member, filteredKeyword))
                {
                    matchedMembers.Add(member.Clone());
                }
            }
            if (matchedMembers.Count == 0 && filteredKeyword.Length > 0)
            {
                interactor.Poke("🔍 没有找到匹配「" + filteredKeyword + "」的成员（已同时检索 QQ号/QQ昵称/群名片/专属头衔）。提示：可以换关键词的一部分再试。");
                return;
            }

            int total = matchedMembers.Count;
            List<string> previewLines = new();
            foreach (JsonElement member in matchedMembers.Take(10))
            {
                previewLines.Add(FormatMemberBrief(member));
            }
            string moreText = total > 10 ? "\n... 等共 " + total + " 人" : "";
            string header = filteredKeyword.Length > 0
                ? "🔍 匹配「" + filteredKeyword + "」的成员（共" + total + "人）："
                : "📋 群成员列表（共" + total + "人）：";
            LogOperation(filteredKeyword.Length > 0 ? "查找群成员" : "获取成员列表",
                operatorId ?? "system", "", "成功, 共" + total + "人");
            interactor.Poke(header + "\n" + string.Join("\n", previewLines) + moreText);
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("获取指定群成员详细信息。userId和keyword二选一：keyword同时匹配QQ号/QQ昵称/群名片/专属头衔，唯一命中直接返回详情，多个命中列出候选")]
    public async Task QueryMemberInfo(
        [Description("群号")] long groupId,
        [Description("要查询的QQ号，可留空改用keyword")] long? userId = null,
        [Description("可选搜索关键词")] string keyword = null,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        try
        {
            long resolvedUserId = userId ?? 0;
            string filteredKeyword = (keyword ?? "").Trim();
            if (resolvedUserId == 0 && filteredKeyword.Length > 0)
            {
                JsonObject listParameters = new() { ["group_id"] = groupId };
                string listResponse = await CallActionAsync("get_group_member_list", listParameters);
                JsonElement members = ParseActionResponse(listResponse, "查找群成员");
                List<JsonElement> matchedMembers = new();
                foreach (JsonElement member in members.EnumerateArray())
                {
                    if (MemberMatchesKeyword(member, filteredKeyword))
                    {
                        matchedMembers.Add(member.Clone());
                    }
                }
                if (matchedMembers.Count == 0)
                {
                    interactor.Poke("🔍 没有找到匹配「" + filteredKeyword + "」的成员（已同时检索 QQ号/QQ昵称/群名片/专属头衔）。");
                    return;
                }
                if (matchedMembers.Count > 1)
                {
                    List<string> candidateLines = new()
                    {
                        "🔍「" + filteredKeyword + "」匹配到 " + matchedMembers.Count + " 位成员，请用 QQ号 指定要查询的对象："
                    };
                    foreach (JsonElement member in matchedMembers.Take(10))
                    {
                        candidateLines.Add("- " + FormatMemberBrief(member));
                    }
                    interactor.Poke(string.Join("\n", candidateLines));
                    return;
                }
                resolvedUserId = GetNumericField(matchedMembers[0], "user_id");
            }
            if (resolvedUserId == 0)
            {
                interactor.Poke("❌ 请提供要查询的 QQ号（userId）或搜索关键词（keyword）");
                return;
            }

            JsonObject infoParameters = new()
            {
                ["group_id"] = groupId,
                ["user_id"] = resolvedUserId
            };
            string infoResponse = await CallActionAsync("get_group_member_info", infoParameters);
            JsonElement memberData = ParseActionResponse(infoResponse, "获取成员信息");

            string role = GetStringField(memberData, "role");
            string roleDisplay = role switch
            {
                "owner" => "群主",
                "admin" => "管理员",
                "member" => "普通成员",
                _ => role
            };
            List<string> infoLines = new()
            {
                "📋 成员信息：",
                "QQ号: " + GetStringField(memberData, "user_id"),
                "昵称: " + GetStringField(memberData, "nickname"),
                "群名片: " + (GetStringField(memberData, "card").Length > 0 ? GetStringField(memberData, "card") : "未设置"),
                "群等级: " + GetStringField(memberData, "level"),
                "头衔: " + (GetStringField(memberData, "title").Length > 0 ? GetStringField(memberData, "title") : "无"),
                "入群时间: " + FormatTimestamp(GetNumericField(memberData, "join_time")),
                "最后发言: " + FormatTimestamp(GetNumericField(memberData, "last_sent_time")),
                "身份: " + roleDisplay
            };
            if (GetNumericField(memberData, "shut_up_timestamp") > 0)
            {
                infoLines.Add("⛔ 当前处于禁言状态");
            }
            LogOperation("获取成员信息", operatorId ?? "system", resolvedUserId.ToString(), "成功");
            interactor.Poke(string.Join("\n", infoLines));
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    // ============================================================
    // AI 函数：可选功能
    // ============================================================

    [XmlFunction(FunctionMode.OneShot)]
    [Description("【高危】踢出指定群成员，需在插件配置中启用踢人功能")]
    public async Task KickMember(
        [Description("群号")] long groupId,
        [Description("要踢出的QQ号")] long userId,
        [Description("是否同时拒绝该用户的加群申请，默认否")] bool rejectAddRequest = false,
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
            string rejectText = rejectAddRequest ? "，已拒绝加群申请" : "";
            LogOperation("踢出成员【高危】", operatorId ?? "system", userId.ToString(), "成功" + rejectText);
            interactor.Poke("✅ 已将用户 " + userId + " 踢出群聊" + rejectText);
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("【高危】开启或关闭全员禁言，需在插件配置中启用全员禁言功能")]
    public async Task WholeGroupBan(
        [Description("群号")] long groupId,
        [Description("true开启全员禁言，false关闭")] bool enable,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (Gate(Configuration.EnableWholeBan, "全员禁言"))
        {
            return;
        }
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        try
        {
            JsonObject parameters = new()
            {
                ["group_id"] = groupId,
                ["enable"] = enable
            };
            string response = await CallActionAsync("set_group_whole_ban", parameters);
            ParseActionResponse(response, "全员禁言");
            string actionText = enable ? "开启" : "关闭";
            LogOperation(actionText + "全员禁言【高危】", operatorId ?? "system", "", "成功");
            interactor.Poke("✅ 已" + actionText + "全员禁言");
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("发布群公告，需在插件配置中启用群公告功能。编辑公告=先用QueryGroupNotices读旧公告，改写后发布新版")]
    public async Task PublishGroupNotice(
        [Description("群号")] long groupId,
        [Description("公告正文")] string content,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (Gate(Configuration.EnableGroupNotice, "群公告"))
        {
            return;
        }
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        try
        {
            JsonObject parameters = new()
            {
                ["group_id"] = groupId,
                ["content"] = content
            };
            string response = await CallActionAsync("_send_group_notice", parameters);
            ParseActionResponse(response, "发布群公告");
            string preview = content.Length > 50 ? content[..50] + "..." : content;
            LogOperation("发布群公告", operatorId ?? "system", "", "成功: " + preview);
            interactor.Poke("✅ 群公告已发布：" + preview);
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("读取最近的群公告列表（含notice_id），需在插件配置中启用群公告功能。编辑公告前先用本工具读取")]
    public async Task QueryGroupNotices(
        [Description("群号")] long groupId,
        [Description("返回最近几条公告，默认5")] int count = 5,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (Gate(Configuration.EnableGroupNotice, "群公告"))
        {
            return;
        }
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        try
        {
            JsonObject parameters = new() { ["group_id"] = groupId };
            string response = await CallActionAsync("_get_group_notice", parameters);
            JsonElement notices = ParseActionResponse(response, "读取群公告");
            List<JsonElement> sortedNotices = notices.EnumerateArray()
                .OrderByDescending(notice => GetNumericField(notice, "publish_time"))
                .Take(Math.Max(1, count))
                .Select(notice => notice.Clone())
                .ToList();
            if (sortedNotices.Count == 0)
            {
                interactor.Poke("📋 本群暂无群公告");
                return;
            }
            List<string> lines = new() { "📋 最近 " + sortedNotices.Count + " 条群公告：" };
            foreach (JsonElement notice in sortedNotices)
            {
                string noticeId = GetStringField(notice, "notice_id");
                string noticeText = "";
                if (notice.TryGetProperty("message", out JsonElement messageElement)
                    && messageElement.ValueKind == JsonValueKind.Object
                    && messageElement.TryGetProperty("text", out JsonElement textElement)
                    && textElement.ValueKind == JsonValueKind.String)
                {
                    noticeText = textElement.GetString() ?? "";
                }
                string idText = noticeId.Length > 0 ? "\nnotice_id=" + noticeId : "";
                lines.Add("---\n[" + FormatTimestamp(GetNumericField(notice, "publish_time")) + "] 发布者: "
                    + GetStringField(notice, "sender_id") + idText + "\n" + noticeText.Trim());
            }
            LogOperation("读取群公告", operatorId ?? "system", "", "成功, 共" + sortedNotices.Count + "条");
            interactor.Poke(string.Join("\n", lines));
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("删除指定群公告（NapCat扩展接口），noticeId从QueryGroupNotices获取。需在插件配置中启用群公告功能")]
    public async Task DeleteGroupNotice(
        [Description("群号")] long groupId,
        [Description("要删除的公告ID")] string noticeId,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (Gate(Configuration.EnableGroupNotice, "群公告"))
        {
            return;
        }
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        try
        {
            JsonObject parameters = new()
            {
                ["group_id"] = groupId,
                ["notice_id"] = noticeId
            };
            // NapCat 用 _del_group_notice，LLOneBot 用 _delete_group_notice，参数相同动作名不同，依次尝试
            Exception lastError = new Exception("接口调用失败");
            bool called = false;
            foreach (string deleteAction in new[] { "_del_group_notice", "_delete_group_notice" })
            {
                try
                {
                    string response = await CallActionAsync(deleteAction, parameters);
                    ParseActionResponse(response, "删除群公告");
                    called = true;
                    break;
                }
                catch (Exception attemptError)
                {
                    lastError = attemptError;
                }
            }
            if (called == false)
            {
                throw lastError;
            }
            LogOperation("删除群公告", operatorId ?? "system", "", "成功, notice_id: " + noticeId);
            interactor.Poke("✅ 已删除群公告 (ID: " + noticeId + ")");
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("将指定消息设为精华，需在插件配置中启用精华消息功能")]
    public async Task SetEssenceMessage(
        [Description("要设为精华的消息ID")] string messageId,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (Gate(Configuration.EnableEssence, "精华消息"))
        {
            return;
        }
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        try
        {
            JsonObject parameters = new() { ["message_id"] = messageId };
            string response = await CallActionAsync("set_essence_msg", parameters);
            ParseActionResponse(response, "设置精华");
            LogOperation("设置精华", operatorId ?? "system", "", "成功, 消息ID: " + messageId);
            interactor.Poke("✅ 已将消息 (ID: " + messageId + ") 设为精华");
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("取消指定消息的精华，需在插件配置中启用精华消息功能")]
    public async Task UnsetEssenceMessage(
        [Description("要取消精华的消息ID")] string messageId,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (Gate(Configuration.EnableEssence, "精华消息"))
        {
            return;
        }
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        try
        {
            JsonObject parameters = new() { ["message_id"] = messageId };
            string response = await CallActionAsync("delete_essence_msg", parameters);
            ParseActionResponse(response, "取消精华");
            LogOperation("取消精华", operatorId ?? "system", "", "成功, 消息ID: " + messageId);
            interactor.Poke("✅ 已取消消息 (ID: " + messageId + ") 的精华");
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看群精华消息列表，需在插件配置中启用精华消息功能")]
    public async Task ListEssenceMessages(
        [Description("群号")] long groupId,
        [Description("返回最近几条精华，默认10")] int count = 10,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (Gate(Configuration.EnableEssence, "精华消息"))
        {
            return;
        }
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        try
        {
            JsonObject parameters = new() { ["group_id"] = groupId };
            // 三端列表动作都是 get_essence_msg_list；未实现的端回 1404，由 ParseActionResponse 报「协议端不支持」
            string response = await CallActionAsync("get_essence_msg_list", parameters);
            JsonElement items = ParseActionResponse(response, "获取精华列表");
            List<JsonElement> allItems = items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray().ToList()
                : new List<JsonElement>();
            if (allItems.Count == 0)
            {
                // 空列表时打印原始返回，便于定位协议端返回结构与预期不符的情况
                logger.LogInformation("[群管] 精华列表为空，原始返回: {Raw}",
                    response.Length > 500 ? response[..500] : response);
                interactor.Poke("📋 本群暂无精华消息");
                return;
            }
            List<string> lines = new() { "📋 群精华消息（共 " + allItems.Count + " 条）：" };
            foreach (JsonElement item in allItems.Take(Math.Max(1, count)))
            {
                string messageId = GetStringField(item, "message_id");
                string sender = GetStringField(item, "sender_nick");
                if (sender.Length == 0)
                {
                    sender = GetStringField(item, "sender_id");
                }
                // 时间兜底：无 sender_time 时用 operator_time，再无则按 message_id 反查原消息时间
                long messageTime = GetNumericField(item, "sender_time");
                string operatorText = "";
                string essenceOperator = GetStringField(item, "operator_uin");
                if (essenceOperator.Length == 0)
                {
                    essenceOperator = GetStringField(item, "operator_id");
                }
                if (essenceOperator.Length > 0)
                {
                    operatorText = "，设置人 " + essenceOperator;
                }
                (string content, long resolvedTime) = await ExtractEssenceContentAsync(item, messageId, messageTime);
                messageTime = resolvedTime;
                string idLabel = messageId.Length > 0 ? "id " + messageId : "无 message_id（该协议端不支持取消精华）";
                lines.Add("- [" + FormatTimestamp(messageTime) + "] " + sender + ": " + content + "\n  " + idLabel + operatorText);
            }
            if (allItems.Count > Math.Max(1, count))
            {
                lines.Add("... 仅显示前 " + Math.Max(1, count) + " 条");
            }
            LogOperation("获取精华列表", operatorId ?? "system", "", "成功, 共" + allItems.Count + "条");
            interactor.Poke(string.Join("\n", lines));
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("给群成员设置专属头衔，需在插件配置中启用且Bot必须是群主（仅管理员会失败）。title传空字符串取消头衔")]
    public async Task SetSpecialTitle(
        [Description("群号")] long groupId,
        [Description("目标QQ号")] long userId,
        [Description("头衔文字，空字符串取消头衔")] string title,
        [Description("有效天数，-1为永久。注意：NapCat/LLOneBot/SnowLuma 三端实测都忽略该参数，头衔一律长期有效，不要向用户承诺到期时间")] int durationDays = -1,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (Gate(Configuration.EnableSpecialTitle, "专属头衔"))
        {
            return;
        }
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
                ["special_title"] = title,
                ["duration"] = durationDays
            };
            string response = await CallActionAsync("set_group_special_title", parameters);
            ParseActionResponse(response, "设置专属头衔");
            string display = title.Length > 0 ? title : "(取消头衔)";
            LogOperation("设置专属头衔", operatorId ?? "system", userId.ToString(), "成功: " + display);
            interactor.Poke("✅ 已设置用户 " + userId + " 的专属头衔为: " + display);
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("主动查看当前待处理的加群申请列表（申请人、验证消息、request_flag）。需在插件配置中启用加群申请处理")]
    public async Task CheckJoinRequests(
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (Gate(Configuration.EnableJoinRequest, "加群申请"))
        {
            return;
        }
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        try
        {
            List<JsonElement> pendingRequests = await FetchPendingJoinRequestsAsync();
            if (pendingRequests.Count == 0)
            {
                interactor.Poke("📋 当前没有待处理的加群申请");
                return;
            }
            List<string> lines = new() { "📋 待处理加群申请（共 " + pendingRequests.Count + " 条）：" };
            foreach (JsonElement request in pendingRequests)
            {
                string comment = GetStringField(request, "message").Trim();
                if (comment.Length == 0)
                {
                    comment = "(无验证消息)";
                }
                lines.Add("- 群" + GetStringField(request, "group_id") + " | "
                    + RequesterNick(request) + "(" + RequesterUin(request) + ")"
                    + "\n  验证消息：" + comment + "\n  request_flag=" + JoinRequestFlag(request));
            }
            lines.Add("使用 HandleJoinRequest 并传入对应 request_flag 来通过或拒绝。");
            interactor.Poke(string.Join("\n", lines));
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ 获取加群申请失败: " + operationError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("审批加群申请：通过或拒绝。flag从加群申请事件或CheckJoinRequests获取。需在插件配置中启用加群申请处理")]
    public async Task HandleJoinRequest(
        [Description("申请标识 request_flag")] string flag,
        [Description("true通过，false拒绝")] bool approve,
        [Description("拒绝理由（仅拒绝时有效）")] string reason = null,
        [Description("请求类型，add或invite，默认add")] string subType = "add",
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (Gate(Configuration.EnableJoinRequest, "加群申请"))
        {
            return;
        }
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        try
        {
            JsonObject parameters = new()
            {
                ["flag"] = flag,
                ["sub_type"] = subType ?? "add",
                ["approve"] = approve,
                ["reason"] = reason ?? ""
            };
            string response = await CallActionAsync("set_group_add_request", parameters);
            ParseActionResponse(response, "审批加群申请");
            string actionText = approve ? "通过" : "拒绝";
            LogOperation("审批加群申请", operatorId ?? "system", "", "成功: " + actionText + ", flag=" + flag);
            interactor.Poke("✅ 已" + actionText + "该加群申请");
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ " + operationError.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("拿不准的群管决策（如加群申请、敏感操作）时，私聊询问主人后再决定。需在插件配置中填写主人QQ号")]
    public async Task AskMaster(
        [Description("要问主人的内容，应包含足够背景信息")] string question,
        [Description("发起操作的用户QQ号，自主决策填system")] string operatorId = "system")
    {
        if (GateOperator(operatorId ?? "system"))
        {
            return;
        }
        long masterId = ParseQQNumber(Configuration.MasterQQ);
        if (masterId == 0)
        {
            interactor.Poke("❌ 未配置主人QQ，无法询问主人，请自行谨慎判断");
            return;
        }
        try
        {
            await SendPrivateTextAsync(masterId, "❓ 群管决策请示：\n" + question);
            LogOperation("询问主人", operatorId ?? "system", Configuration.MasterQQ, "成功");
            interactor.Poke("✅ 已私聊主人，等待主人回复。在主人回复前，先不要执行该操作。");
        }
        catch (Exception operationError)
        {
            interactor.Poke("❌ 询问主人失败: " + operationError.Message);
        }
    }

    // ============================================================
    // 辅助方法
    // ============================================================

    static bool MemberMatchesKeyword(JsonElement member, string keyword)
    {
        string lowerKeyword = keyword.ToLowerInvariant();
        return GetStringField(member, "user_id").ToLowerInvariant().Contains(lowerKeyword)
            || GetStringField(member, "nickname").ToLowerInvariant().Contains(lowerKeyword)
            || GetStringField(member, "card").ToLowerInvariant().Contains(lowerKeyword)
            || GetStringField(member, "title").ToLowerInvariant().Contains(lowerKeyword);
    }

    // 成员简表一行：名片(QQ号) 或 昵称(QQ号)，可选附带专属头衔
    string FormatMemberBrief(JsonElement member)
    {
        string userId = GetStringField(member, "user_id");
        string card = GetStringField(member, "card").Trim();
        string nickname = GetStringField(member, "nickname").Trim();
        string display = card.Length > 0 ? card + "(" + userId + ")" : nickname + "(" + userId + ")";
        if (Configuration.ShowTitleInQuery)
        {
            string title = GetStringField(member, "title").Trim();
            if (title.Length > 0)
            {
                display += " 🏷️" + title;
            }
        }
        return display;
    }

    // 精华正文提取（对齐上游 v1.2.4）：兼容 NapCat/新版SnowLuma 的 content 段数组（非文本段渲染占位符）、
    // content 纯字符串、旧版 SnowLuma 的 msg_content 透传格式（msg_type 1=文本/2=表情/3=图片/4=文件）、
    // LLOneBot 无 content 时按 message_id 用 get_msg 反查正文（单条失败不影响整列表）；
    // 全部拿不到时诚实返回 [内容不可用]。
    async Task<(string Content, long Time)> ExtractEssenceContentAsync(JsonElement item, string messageId, long messageTime)
    {
        // 1) content 为纯字符串
        if (item.TryGetProperty("content", out JsonElement contentElement))
        {
            if (contentElement.ValueKind == JsonValueKind.String)
            {
                string direct = (contentElement.GetString() ?? "").Trim();
                if (direct.Length > 0)
                {
                    return (TruncateEssence(direct), messageTime);
                }
            }
            else if (contentElement.ValueKind == JsonValueKind.Array)
            {
                string built = ExtractSegmentText(contentElement).Trim();
                if (built.Length > 0)
                {
                    return (TruncateEssence(built), messageTime);
                }
            }
        }

        // 2) 旧版 SnowLuma：msg_content 透传 QQ web 原始格式
        if (item.TryGetProperty("msg_content", out JsonElement msgContent))
        {
            string webContent = ExtractWebEssenceContent(msgContent).Trim();
            if (webContent.Length > 0)
            {
                return (TruncateEssence(webContent), messageTime);
            }
        }

        // 3) LLOneBot：只返回元数据无 content，按 message_id 用 get_msg 反查正文（单条失败不影响整列表）
        if (messageId.Length > 0)
        {
            try
            {
                JsonObject lookupParameters = new() { ["message_id"] = messageId };
                string response = await CallActionAsync("get_msg", lookupParameters);
                JsonElement data = ParseActionResponse(response, "获取消息");
                JsonElement message = data.TryGetProperty("message", out JsonElement messageElement)
                    ? messageElement.Clone()
                    : JsonDocument.Parse("[]").RootElement.Clone();
                string lookedUp = ExtractSegmentText(message).Trim();
                if (lookedUp.Length > 0)
                {
                    // 顺带兜底时间：LLOneBot 精华条目常无 sender_time，get_msg 的时间更可靠
                    if (messageTime <= 0)
                    {
                        long lookedTime = GetNumericField(data, "time");
                        if (lookedTime > 0)
                        {
                            messageTime = lookedTime;
                        }
                    }
                    return (TruncateEssence(lookedUp), messageTime);
                }
            }
            catch
            {
                // 单条反查失败不影响整列表
            }
        }

        // 兜底时间：operator_time
        if (messageTime <= 0)
        {
            long operatorTime = GetNumericField(item, "operator_time");
            if (operatorTime > 0)
            {
                messageTime = operatorTime;
            }
        }
        return ("[内容不可用]", messageTime);
    }

    // OneBot 段数组 → 正文（非文本段渲染为占位符，不再静默丢弃）
    static string ExtractSegmentText(JsonElement message)
    {
        StringBuilder builder = new();
        if (message.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement segment in message.EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                string segmentType = GetStringField(segment, "type");
                if (segmentType == "text")
                {
                    if (segment.TryGetProperty("data", out JsonElement segmentData) && segmentData.ValueKind == JsonValueKind.Object)
                    {
                        builder.Append(GetStringField(segmentData, "text"));
                    }
                }
                else if (segmentType is "image" or "flash")
                {
                    builder.Append("[图片]");
                }
                else if (segmentType is "face" or "mface")
                {
                    builder.Append("[表情]");
                }
                else if (segmentType == "record")
                {
                    builder.Append("[语音]");
                }
                else if (segmentType == "video")
                {
                    builder.Append("[视频]");
                }
                else if (segmentType is "json" or "xml")
                {
                    builder.Append("[卡片]");
                }
                else if (segmentType.Length > 0)
                {
                    builder.Append("[").Append(segmentType).Append("]");
                }
            }
        }
        return builder.ToString();
    }

    // 旧版 SnowLuma 透传的 QQ web 原始精华格式：msg_type 1=文本/2=表情/3=图片/4=文件
    static string ExtractWebEssenceContent(JsonElement msgContent)
    {
        StringBuilder builder = new();
        if (msgContent.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement segment in msgContent.EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                long msgType = GetNumericField(segment, "msg_type");
                string text = GetStringField(segment, "text");
                builder.Append(msgType switch
                {
                    1 => text,
                    2 => "[表情]",
                    3 => "[图片]",
                    4 => "[文件]",
                    _ => ""
                });
            }
        }
        else if (msgContent.ValueKind == JsonValueKind.Object)
        {
            long msgType = GetNumericField(msgContent, "msg_type");
            string text = GetStringField(msgContent, "text");
            builder.Append(msgType switch
            {
                1 => text,
                2 => "[表情]",
                3 => "[图片]",
                4 => "[文件]",
                _ => ""
            });
        }
        return builder.ToString();
    }

    static string TruncateEssence(string content)
    {
        return content.Length > 80 ? content[..80] + "..." : content;
    }

    async Task<List<JsonElement>> FetchPendingJoinRequestsAsync()
    {
        string response = await CallActionAsync("get_group_system_msg", new JsonObject());
        return ExtractPendingJoinRequests(response);
    }

    List<JsonElement> ExtractPendingJoinRequests(string responseText)
    {
        JsonElement data = ParseActionResponse(responseText, "获取群系统消息");
        List<JsonElement> pendingRequests = new();
        foreach (JsonElement requestItem in ExtractJoinRequests(data, responseText))
        {
            if (IsPendingJoinRequest(requestItem))
            {
                pendingRequests.Add(requestItem);
            }
        }
        return pendingRequests;
    }

    // 申请列表的位置分两种：NapCat 与 LLOneBot 回对象、申请在 join_requests，SnowLuma 直接回数组
    List<JsonElement> ExtractJoinRequests(JsonElement data, string responseText)
    {
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("join_requests", out JsonElement nestedRequests)
            && nestedRequests.ValueKind == JsonValueKind.Array)
        {
            return nestedRequests.EnumerateArray().Select(request => request.Clone()).ToList();
        }
        if (data.ValueKind == JsonValueKind.Array)
        {
            return data.EnumerateArray().Select(request => request.Clone()).ToList();
        }
        // 结构不认识时不能静默当“没有申请”，否则换协议端以后无从定位
        logger.LogInformation("[群管] 加群申请返回结构不认识（协议端 {Provider}），原始返回: {Raw}",
            providerName.Length > 0 ? providerName : "未识别",
            responseText.Length > 500 ? responseText[..500] : responseText);
        return new List<JsonElement>();
    }

    // 已处理标记两种写法都认：实测 LLOneBot 回 checked=true（同时 actor=0），NapCat 系用 actor 非 0
    static bool IsPendingJoinRequest(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        string actor = GetStringField(request, "actor");
        bool handled = request.TryGetProperty("checked", out JsonElement checkedElement)
            && (checkedElement.ValueKind == JsonValueKind.True
                || checkedElement.ValueKind == JsonValueKind.Number && checkedElement.GetInt32() != 0);
        if (actor.Length > 0 && actor != "0" || handled)
        {
            return false;
        }
        // 只认 request_id 会把 SnowLuma 的申请整批丢掉（它给的是 flag），标识取到任一才算有待处理
        return JoinRequestFlag(request).Length > 0;
    }

    static string GetStringField(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        if (element.TryGetProperty(fieldName, out JsonElement field) == false)
        {
            return "";
        }
        return field.ValueKind switch
        {
            JsonValueKind.String => field.GetString() ?? "",
            JsonValueKind.Number => field.GetRawText(),
            _ => ""
        };
    }

    static long GetNumericField(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }
        if (element.TryGetProperty(fieldName, out JsonElement field) == false)
        {
            return 0;
        }
        return field.ValueKind switch
        {
            JsonValueKind.Number => field.GetInt64(),
            JsonValueKind.String => long.TryParse(field.GetString(), out long parsed) ? parsed : 0,
            _ => 0
        };
    }

    static long ParseQQNumber(string text)
    {
        return long.TryParse((text ?? "").Trim(), out long parsed) ? parsed : 0;
    }

    static string FormatTimestamp(long unixSeconds)
    {
        if (unixSeconds <= 0)
        {
            return "N/A";
        }
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.ToString(
                "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch
        {
            return unixSeconds.ToString(CultureInfo.InvariantCulture);
        }
    }
}