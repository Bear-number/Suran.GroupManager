
# Suran.GroupManager

QQ 群聊管家，移植自 KiraAI 的 Group-Manager-Plugin，功能对齐：禁言/踢人/名片/撤回/成员查询/群公告/精华消息/专属头衔/加群申请审批/成员变动感知。所有操作通过 OneBot 正向 WebSocket 直发协议端，插件自持连接（常驻接收循环统一处理响应与事件，断线自动重连）。

## 版本
4.1.0（对应 Alife 客户端 4.2.x）

## 依赖
- Alife.Function.FunctionCaller

## 配置
| 键 | 默认 | 说明 |
|---|---|---|
| OneBotWsUrl | ws://127.0.0.1:3001 | OneBot 正向 WebSocket 地址 |
| AdminList | 空 | 管理员QQ列表，英文逗号分隔；关闭AI自主执行时只有这些人和系统可调用 |
| AllowAiAutonomous | 开 | 允许AI自主执行（AI自行决定的操作直接放行） |
| AutoCheckAdmin | 开 | 启动后自检 Bot 在各群的管理员身份并写日志 |
| LogOperations | 开 | 所有群管操作写日志 |
| EnableKick | 关 | 高危：踢出成员 |
| EnableWholeBan | 关 | 高危：全员禁言 |
| EnableGroupNotice | 关 | 群公告发布/读取/删除 |
| EnableEssence | 关 | 精华消息设置/取消/查看 |
| EnableSpecialTitle | 关 | 专属头衔（QQ限制仅群主可设，Bot须为群主） |
| ShowTitleInQuery | 开 | 成员查询结果展示专属头衔 |
| EnablePresenceNotice | 关 | 退群/被踢时告知AI |
| EnableWelcome | 关 | 新人入群自动发欢迎语 |
| WelcomeTemplate | 欢迎 {nickname} 加入本群～ | 占位符 {nickname} / {user_id} |
| EnableJoinRequest | 关 | 加群申请轮询与审批（依赖协议端 get_group_system_msg；SnowLuma 未实现，探测到会自动停轮询） |
| JoinPollIntervalMinutes | 10 | 轮询间隔，0为关闭 |
| JoinRequestMode | ask_master | ask_master / auto / notify_only |
| MasterQQ | 空 | 主人QQ号（ask_master / notify_only 必填） |

## AI 函数（18 个）
核心（默认可用）：
- BanMember(groupId, userId, durationSeconds=600, operatorId) - 禁言，秒数0为解禁；UnbanMember 单独解禁
- UnbanMember(groupId, userId, operatorId) - 解除禁言
- SetMemberCard(groupId, userId, card, operatorId) - 设置/取消群名片
- RecallMessage(messageId, operatorId) - 撤回消息
- QueryMemberList(groupId, keyword, operatorId) - 成员列表，keyword 同时匹配QQ号/昵称/名片/头衔
- QueryMemberInfo(groupId, userId, keyword, operatorId) - 成员详情，keyword 唯一命中直接查、多命中给候选

可选（需开配置开关）：
- KickMember(groupId, userId, rejectAddRequest=false) - 踢人【高危】
- WholeGroupBan(groupId, enable) - 全员禁言【高危】
- PublishGroupNotice / QueryGroupNotices / DeleteGroupNotice - 群公告
- SetEssenceMessage / UnsetEssenceMessage / ListEssenceMessages - 精华消息
- SetSpecialTitle(groupId, userId, title, durationDays=-1) - 专属头衔
- CheckJoinRequests / HandleJoinRequest(flag, approve, reason, subType) - 加群申请
- AskMaster(question) - 私聊请示主人

所有管理函数都带 operatorId 参数：AI自主决策时填 system，用户命令时从消息上下文取QQ号。权限规则与 KiraAI 版一致：AllowAiAutonomous 开启时放行，关闭时仅 AdminList 中的QQ号与系统调用可执行。

## 事件能力
- 成员变动：退群/被踢/被踢出（kick_me）实时告知AI；入群自动发欢迎语（均可关）
- 变动与加群申请提示都带「群名(群号)」，多群 Bot 能一眼看出是哪个群；群名用 get_group_info 取并按群缓存，取不到退回群号
- 加群申请：按分钟级轮询 get_group_system_msg，首轮只建基线不通知，新申请按模式分发——ask_master/auto 推给AI决策（附防注入提示），notify_only 私聊主人
- 已处理申请自动过滤，兼容 NapCat/LLOneBot（data.join_requests + actor）与 SnowLuma（data 数组 + checked）两种返回格式

## 协议端兼容（NapCat / LLOneBot / SnowLuma）
- 启动 5 秒后用 get_version_info 识别协议端（`app_name`、`name`、`vendor` 三字段全取，实测 LLOneBot 只回 app_name），识别结果写普通日志并在错误里署名
- 接口未实现（retcode 1404）时报「协议端 X 未实现该接口，请检查协议端设置或更换支持的协议端」，不再只丢一个裸错误码
- 加群申请条目字段按端回退：`requester_uin`→`invitor_uin`→`user_id`、`requester_nick`→`nickname`、`flag`→`request_id`。本机实测 LLOneBot 7.12.14 的 `get_group_system_msg` 回 `data.join_requests`，条目含 `request_id`/`requester_uin`/`requester_nick`/`message`/`group_id`/`group_name`/`checked`/`actor`（**没有 flag**，标识只能用 request_id），已处理标记是 `checked=true`（bool 与数字两种写法都接受）
- NapCat 的申请条目里申请人 UIN 发在 `invitor_uin` 上，既没有 `requester_uin` 也没有 `user_id`，且 `requester_nick` 与 `invitor_nick` 同值（它自己源码的怪癖），所以必须留 invitor_uin 这一路回退，否则通知里申请人 QQ 空着
- 三端的 `set_group_add_request` 都要求 flag 是字符串，且都能把 request_id 当 flag 解析，SnowLuma 也收纯数字，回退是安全的
- **SnowLuma 没有注册 `get_group_system_msg`（实测回 1404）**：轮询探测到不支持时打一条带协议端署名的日志并停止轮询，不每轮重复刷同一句话；SnowLuma 用户要靠加群申请事件或换 NapCat/LLOneBot
- 群公告删除动作名两端不同（NapCat `_del_group_notice` / LLOneBot `_delete_group_notice`），依次尝试；精华列表统一 `get_essence_msg_list`（`get_group_essence_msg_list` 三端都不存在，已移除无效探测）
- 专属头衔的 `duration` 参数三端实测都忽略，头衔一律长期有效，函数描述里已注明不要向用户承诺到期时间
- 精华/公告返回结构不认识或列表为空时打印原始返回（截断 500 字），远程用户不用开详细日志也能回报

## 安全设计（继承自原插件）
- 分层权限：AI自主执行 vs 管理员命令，独立开关
- 高危功能默认关闭，独立开关
- 操作审计日志（LogOperations）
- 申请人的昵称/验证消息明确标注为不可信数据，防止指令注入

## 与 KiraAI 原版的差异
- 群号/操作者改为函数参数显式传入（Alife 的 XmlFunction 没有 KiraAI 的会话上下文），AI 从 QChat 注入的群聊上下文取值
- 加群申请的 ask_master/auto 模式通过 Poke 推送给AI决策（KiraAI 是合成群消息事件），AI 收到后可直接调 HandleJoinRequest
- 申请去重记录保存在内存（插件重载后重建基线，不会重复打扰）；KiraAI 版有文件持久化
- 与 group_member_viewer 的自动卸载共存逻辑不适用 Alife，未移植
