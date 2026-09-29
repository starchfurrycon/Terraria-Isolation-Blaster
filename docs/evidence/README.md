# 真机证据记录（2026-09-29）

两份证据都来自用户真机测试，都是离线检查（`verify-all.ps1`、本地测试）碰不到、只有真机能暴露的问题。
保留原始日志，是为了以后不再把「本地全过」当成「真机可用」。

## 一、进世界即闪退：插件依赖没装进游戏目录

副本 `D:\zhadai-test\game` 第一次真机测试时进入世界即闪退。游戏自己写的 `client-crashlog.txt`
原文保存在 `2026-09-29-crash-missing-automation.txt`，关键两行：

    System.IO.FileNotFoundException: 未能加载文件或程序集"ZhaDai.Automation, Version=0.1.0.0, ..."
       在 ZhaDai.Runtime.Host.Frame()
       在 Terraria.Main.UpdateWorld_Players()

结论与修法：

- 安装器只部署了 `ZhaDai.Runtime.dll`，而它引用的 `ZhaDai.Automation.dll` 没进游戏目录。
  这是注入型方案里最容易漏的一环：程序集解析只看应用程序目录，不看插件原先放在哪。
- 安装器现在读插件程序集自己的引用表，把同目录下的 `ZhaDai.*` 依赖一并部署；任何一个缺失就直接
  在 `install` 阶段抛错（失败在安装时是一条消息，失败在游戏里是一次崩溃）。
- `Hooks` 的三个入口都套了 try/catch。类型加载失败发生在**调用方**，宿主方法体内的 try/catch 覆盖不到，
  所以这一层必须加在契约面上。
- `tools/verify-all.ps1` 新增一步「plugin payload carries its own dependencies」，检查构建输出旁边
  是否真的躺着每个 `ZhaDai.*` 引用，防止再犯。
- 顺带发现：`run.cfg` 因为 PowerShell 里 `@('a' + $x, 'b')` 的优先级问题被拼成了一行，插件读不到任何
  配置。生成脚本已加括号并在生成时按行数核对（必须 7 行）。

## 二、修好依赖后按 F10 毫无反应

游戏不崩了，但按 F10 没有反应。`runtime.log` 说明插件一切正常（反射自检通过、施工文件读到），
所以问题在按键检测这一段，而不是插件没起来。

根因是反射里一个经典错误：

    object[] pressed = pressedKeysMethod.Invoke(state, null) as object[];   // 恒为 null

`KeyboardState.GetPressedKeys()` 返回的是 `Microsoft.Xna.Framework.Input.Keys[]`。用 GAC 里的
`Microsoft.Xna.Framework.dll` 反射核对元数据：

    KeyboardState.GetPressedKeys() 返回：Microsoft.Xna.Framework.Input.Keys[]
    元素类型 Microsoft.Xna.Framework.Input.Keys，IsValueType = True

值类型数组**不能**协变成 `object[]`，所以 `as object[]` 永远是 null，`HotkeyDown()` 永远返回 false。
成员核对只确认「成员存在」，本地测试也跑不到这条路径（需要真游戏），只有真机能暴露它。

修法与防护：

- 改用 `System.Array` 遍历；匹配逻辑抽成 `ZhaDai.Automation.HotkeyMatch` 并加回归测试
  （其中一条专门用枚举数组验证值类型数组可遍历）。
- 启动时把按键通道状态写进 `runtime.log`（`ReportKeyChannel`），读不到就提示改用 `enabled=1`，
  不再出现「按键没反应且无从解释」。
- 修掉 `run.cfg` 顺序陷阱：`enabled=` 写在 `hotkey=` 之前时，原来边读边判会把带热键的配置当成没热键。
