# Suran.GroupManager

群管理与审核：禁言、踢人、改名片、公告、精华、入群审批，以及操作日志。

## 功能

- 成员：禁言 / 解禁、踢人、改名片、批量禁言与踢人、全员禁言与解除
- 查询：成员列表与详情、被禁言成员、成员变动记录
- 群资料：改群名、查群详情、设管理员、设专属头衔
- 消息：撤回单条、撤回某人最近若干条
- 公告：发布、查询、删除
- 精华消息：设置、取消、列出
- 入群审批：查申请、批准 / 拒绝、问问群主
- 操作日志：谁做了什么，可查

## 安装

先装依赖：`Alife.Function.FunctionCaller`。

装法二选一：插件市场里搜 `Suran.GroupManager`；或把目录放到 `Storage/Plugins/Suran.GroupManager/`，重启角色生效。

## 配置

在设置页里改，改完重启角色生效。

| 设置项 | 键 | 说明 | 默认 |
| --- | --- | --- | --- |
| 运行·功能文档常驻 | `DocumentAlwaysResident` | 开：功能说明常驻系统提示词，模型随时看得到工具；关：隐式加载省 token，模型先输出入口标签再读文档。群管多是主人明确下令时用，默认关 | `false` |
| 基础·协议端地址 | `OneBotWsUrl` | 留空 = 自动跟随「QQ 聊天」插件的配置（推荐）。填了就用这里的，例如 ws://127.0.0.1:3001 | `""` |
| 基础·访问令牌 | `OneBotToken` | 与协议端配置的 token 一致。留空 = 跟随「QQ 聊天」插件。填了地址却没填这里，协议端一开 token 就会连不上 | `""` |
| 权限·管理员QQ列表 | `AdminList` | 可以用群管功能的QQ号，英文逗号分隔；关闭AI自主执行时只有这些人和系统可以调用 | `""` |
| 权限·允许AI自主执行 | `AllowAiAutonomous` | 开启后AI自行决定的群管操作直接放行；关闭后仅管理员QQ列表中的用户命令可执行 | `true` |
| 权限·启动自检Bot管理员身份 | `AutoCheckAdmin` | 启动后检查Bot在各群是否为管理员，不是则写日志提醒 | `true` |
| 权限·记录操作日志 | `LogOperations` | 所有群管操作写入日志，便于追溯 | `true` |
| 高危·启用踢出成员 | `EnableKick` | 关闭时 KickMember 不可用 | `false` |
| 高危·启用全员禁言 | `EnableWholeBan` | 关闭时 WholeGroupBan 不可用 | `false` |
| 功能·启用群公告 | `EnableGroupNotice` | 开启后可发布/读取/删除群公告 | `false` |
| 功能·启用精华消息 | `EnableEssence` | 开启后可设置/取消/查看精华消息 | `false` |
| 功能·启用专属头衔 | `EnableSpecialTitle` | 开启后可设置专属头衔。注意QQ限制仅群主可设，Bot只是管理员时操作会失败 | `false` |
| 功能·查询结果展示头衔 | `ShowTitleInQuery` | 成员查询结果中展示专属头衔 | `true` |
| 感知·启用退群感知 | `EnablePresenceNotice` | 有人退群/被踢时告知AI（注入对话） | `false` |
| 感知·启用入群欢迎 | `EnableWelcome` | 新人入群时自动发欢迎语 | `false` |
| 感知·欢迎语模板 | `WelcomeTemplate` | 占位符 {nickname} 和 {user_id} 会被替换 | `"欢迎 {nickname} 加入本群～"` |
| 欢迎·按群模板 | `WelcomeGroupTemplates` | 多行，每行「群号=模板」，如 123456=欢迎 {nickname} 加入二群～。占位符 {nickname}/{user_id}；命中优先于全局模板，某行解析失败就回退全局模板 | `""` |
| 欢迎·延迟秒 | `WelcomeDelaySeconds` | 新人入群后延迟多少秒发欢迎语，0=立即发，最多60秒 | `0` |
| 欢迎·@新人 | `WelcomeAtNewcomer` | 开启后在欢迎语前 @ 新人，默认关（部分协议端 at 段可能不生效） | `false` |
| 成员变动·记录 | `MemberChangeLog` | 把入群/退群/被踢记录追加到本地 jsonl，供 QueryMemberChanges 查询；只写本机，不依赖协议端，默认开 | `true` |
| 反刷屏·启用 | `AntiSpamEnabled` | 默认关。开启后按重复阈值/关键词/链接检查群消息；动作默认 observe，只写日志不改群 | `false` |
| 反刷屏·群号 | `AntiSpamGroups` | 只对这些群生效，英文或中文逗号分隔；留空=所有群 | `""` |
| 反刷屏·重复阈值 | `AntiSpamRepeatThreshold` | 同一用户10秒内重复发送相同内容达到该次数即命中，默认5；设0关闭重复检测 | `5` |
| 反刷屏·动作 | `AntiSpamAction` | observe=只写日志（默认）/ recall=撤回当前消息 / mute=禁言。填错一律按 observe 处理 | `"observe"` |
| 反刷屏·禁言秒数 | `AntiSpamMuteSeconds` | 动作=mute 时的禁言时长，默认600秒；小于60按60处理 | `600` |
| 反刷屏·关键词 | `AntiSpamKeywords` | 命中任一关键词即触发，英文或中文逗号分隔；留空=不检测。日志只记命中类型与正文长度，不打印正文 | `""` |
| 反刷屏·屏蔽链接 | `AntiSpamBlockLinks` | 开启后消息里出现 http/https/www 等链接即触发，默认关 | `false` |
| 加群·启用申请处理 | `EnableJoinRequest` | 是否定时轮询待处理的加群申请。 | `false` |
| 加群·轮询间隔(分钟) | `JoinPollIntervalMinutes` | 纯API查询不消耗AI额度；0为关闭轮询 | `10` |
| 加群·处理模式 | `JoinRequestMode` | ask_master=有把握就批拿不准私聊问主人 / auto=Bot全权审批 / notify_only=只私聊通知主人 | `"ask_master"` |
| 加群·主人QQ号 | `MasterQQ` | ask_master与notify_only模式必填 | `""` |
