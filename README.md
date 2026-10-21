# cod · 涂山苏苏 Windows 外观包

为官方 Codex Windows 客户端添加苏苏开场、暖白粉色主题和可导入的苏苏宠物素材。启动器和主题使用本机路径配置，可以在另一台电脑、另一个 Codex 账号上安装。

这是个人制作的非官方同人项目。项目不提供 Codex 客户端或账号，不包含原电脑的登录信息、聊天记录和云端宠物记录。

## 下载与安装

先在目标电脑安装官方 Codex Windows 客户端，并使用你自己的账号正常登录一次。当前支持 **Windows x64、默认 C 盘 WindowsApps 安装的商店版**，软件包标识为 `OpenAI.Codex_2p2nqsd0c76g0`；已验证客户端版本为 `26.1013.4894.0`。其他版本或安装位置需要重新验证适配。

有两种下载方式：

| 下载内容 | 安装方式 | 是否需要联网准备工具 |
| --- | --- | --- |
| 仓库页面 **Releases** 中的 Windows x64 完整安装 ZIP | 解压后双击 `Install.cmd`，也可以直接运行 `Setup.cmd` | 已含运行工具，安装包本身可离线使用 |
| 仓库页面 **Code → Download ZIP**，或 Git 克隆的源码 | 解压后双击 `Install.cmd`，先下载固定版本的工具并编译，再执行安装 | 首次需要联网 |

1. 将下载内容**完整解压**到准备长期保留的可写文件夹，例如 `D:\Codex-Susu\`。不要在压缩包里直接运行。
2. 双击根目录的 **Install.cmd**，等候提示完成。源码版首次会准备依赖和编译，通常不需要管理员权限。
3. 保存正在处理的任务，从 Codex 菜单或托盘选择“退出”，完整退出一次。
4. 双击桌面或开始菜单里的 **Codex - 涂山苏苏**。
5. 如果使用新账号，按[宠物导入说明](docs/PET-IMPORT.md)将现成素材导入该账号，再打开 Mini。

完整退出后的启动默认最大化；从托盘或最小化恢复时保留窗口当前大小。主题使用轻量方式覆盖聊天、设置等页面，客户端更新后可能需要调整适配代码。

安装包自带的运行工具只服务于本项目；Codex 的登录和云端功能仍需要其正常网络连接。请保留安装文件夹及其中相邻的 `FoxSpiritCodexBoot`、`TushanSusuTheme`、`TushanSusuPet`、`tools` 目录。

## 不同账号能用吗

**动画和主题可以直接使用新账号。** 它们属于本机外观配置，不依赖原电脑的账号 ID。

**宠物需要导入目标账号一次。** 本项目附带作者 legeling 的现成 v2 兼容精灵图，不必重新生成。登录新账号后，通过支持 Work Pets 的助手导入并选中即可，详见[宠物导入说明](docs/PET-IMPORT.md)。Windows 安装脚本会安装本地外观和入口，不能代替云端宠物导入。

## 恢复与移动

- 取消主题：运行 `TushanSusuTheme\Restore-Theme.cmd`。
- 移除本项目入口：运行 `FoxSpiritCodexBoot\Restore-Shortcuts.cmd`。
- 移动目录：先移除旧入口，移动整个文件夹，再运行 `Install.cmd` 生成新路径。

恢复主题后，正常完整退出 Codex 一次，再从官方入口打开。恢复脚本会检查本项目创建的内容，不覆盖用户后来改动的同名快捷方式。

安装结果见 `setup-result.json`，启动日志见 `FoxSpiritCodexBoot\logs`，主题运行状态见 `TushanSusuTheme\runtime-data`。提交问题时请先检查日志中的本机路径等信息。

## 源码与构建

源码包含启动器、窗口恢复逻辑、主题适配器、安装脚本和素材。Node.js、OpenChatGPTSkin 运行程序及编译后的启动器不提交到源码树；构建时从固定来源准备依赖并核对哈希。首次构建需要联网和 Windows 自带的 .NET Framework 4.x 编译环境。

在项目根目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Package.ps1 -OutputDirectory dist
```

`scripts/Build.ps1` 准备依赖并编译；`scripts/Package.ps1` 生成带有运行工具的完整安装包。打包不安装快捷方式、不启动 Codex，也不上传宠物。自动构建会检查代码和包文件，实际视觉效果仍需在目标客户端验证。

仓库的 **Actions → Build and release Windows package → Run workflow** 只构建并保存安装包 artifact。维护者推送 `v` 开头的版本标签时，工作流会在构建成功后创建对应 GitHub Release。GitHub 自动提供的 “Source code” ZIP 是源码包，完整安装包是 Release 额外附带的 Windows ZIP。

## 来源与许可

- 启动方法参考 [Dragon Codex Boot](https://github.com/yushua0808-cpu/dragon-codex-boot)，保留 MIT 许可和署名。
- 主题运行工具来自 [OpenChatGPTSkin](https://github.com/u2bo/OpenChatGPTSkin)，保留其 MIT 许可及素材权利说明。
- [Node.js](https://nodejs.org/) 及其内嵌组件的完整许可随运行工具保留。
- 苏苏宠物作者为 [legeling](https://github.com/legeling)，来自 [awesome-codex-pet](https://github.com/legeling/awesome-codex-pet/tree/022aea52f75841d31f57b7596479ebb425480a50/pets/tushan-susu--legeling)，采用 **CC BY-NC 4.0，仅限非商业用途**。
- 开场和背景为 AI 生成的非官方同人插画；角色、产品名称及商标属于各自权利人。本项目按个人非商业用途提供素材，不授予角色或商标权利。

根目录 [LICENSE](LICENSE) 适用于本项目原创代码；各目录已有许可继续适用。完整说明见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
