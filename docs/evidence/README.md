# 真机崩溃证据：插件依赖没装进游戏目录（2026-09-29）

副本 D:\zhadai-test\game 第一次真机测试时，进入世界即闪退。游戏自己写的
client-crashlog.txt 原文保存在 2026-09-29-crash-missing-automation.txt，关键两行：

    System.IO.FileNotFoundException: 未能加载文件或程序集"ZhaDai.Automation, Version=0.1.0.0, ..."
       在 ZhaDai.Runtime.Host.Frame()
       在 Terraria.Main.UpdateWorld_Players()

结论与修法：

- 安装器只部署了 ZhaDai.Runtime.dll，而它引用的 ZhaDai.Automation.dll 没进游戏目录。
  这是注入型方案里最容易漏的一环：程序集解析只看应用程序目录，不看插件原先放在哪。
- 安装器现在读插件程序集自己的引用表，把同目录下的 ZhaDai.* 依赖一并部署；任何一个缺失就直接
  在 install 阶段抛错（失败在安装时是一条消息，失败在游戏里是一次崩溃）。
- Hooks 的三个入口都套了 try/catch。类型加载失败发生在**调用方**，宿主方法体内的 try/catch 覆盖不到，
  所以这一层必须加在契约面上。
- 	ools/verify-all.ps1 新增一步「plugin payload carries its own dependencies」，检查构建输出旁边
  是否真的躺着每个 ZhaDai.* 引用，防止再犯。
- 触发这次暴露的顺带收获：un.cfg 因为 PowerShell 里 @('a' + \, 'b') 的优先级问题被拼成了一行，
  插件读不到任何配置。生成脚本已加括号并在生成时按行数核对（必须 7 行）。