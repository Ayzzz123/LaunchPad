# Launchpad

面向 Windows 11 的悬浮式应用启动台。按 `Alt+空格` 打开，也可以单击系统托盘图标；窗口显示在鼠标所在屏幕，点击其他应用或空白区域即可收起。

## 安装

1. 从 [Releases](https://github.com/Ayzzz123/LaunchPad/releases/latest) 下载 `Launchpad-win-x64.zip`。
2. 将压缩包解压到希望长期保留的位置，例如 `%LOCALAPPDATA%\Programs\LaunchPad`。
3. 双击 `Launchpad.exe`。便携版包含运行时，无需另外安装 .NET；程序启动后会驻留在系统托盘，按 `Alt+空格` 打开。

可选：如需登录 Windows 后自动启动，按 `Win+R`，输入 `shell:startup` 并回车；将 `Launchpad.exe` 的快捷方式放入打开的启动文件夹。请先将程序解压到固定位置，再创建快捷方式。

用户添加的应用和布局保存在 `%USERPROFILE%\.launchpad`，删除启动台中的项目不会卸载电脑上的应用。

默认跟随 Windows 深浅色主题，使用系统圆角和阴影；深色界面使用已在本机确认可透出背景的原生Accent Acrylic，表面叠加约9%的半透明黑色薄层；禁用默认Desktop Acrylic，不使用在本机产生黑底的BlurBehind模式。壁纸和后方窗口的变化由 Windows 合成器自动处理，程序不读取壁纸、不生成模糊图片，也不定时检测壁纸。关闭系统透明效果、高对比度或系统不支持时使用实色回退。窗口默认1080×720，居中显示在鼠标所在屏幕的可用区域；图标读取当前桌面大小（本机48像素）。

启动台不会后台自动增加项目。通过右上角“＋”或设置中的“项目管理”手动添加、重命名和移除：选择“添加应用或文件”可多选任意文件，选择“添加磁盘文件夹”可多选目录。应用正常启动，文件使用系统默认程序打开，磁盘文件夹由资源管理器打开；没有关联程序时保留启动台并提示打开失败。快捷方式保留参数、工作目录和图标来源，支持指向文件和目录。配置统一保存在 `%USERPROFILE%\.launchpad`，避免启动来源引起AppData虚拟化、读取两份不同的配置。兼容原有版本2数据，首次使用新目录时从旧的 `%LocalAppData%\Launchpad` 主配置或有效备份迁移，保留旧文件；新目录一旦存在配置或备份便不会被旧目录覆盖。配置继续使用原子替换与有效备份。

点击“整理”或长按图标可调整顺序，点击空白处完成整理并保持当前页面。拖到另一图标中央停留500毫秒创建分组文件夹，拖到页面边缘停留约450毫秒翻页。边缘显示方向及等待进度，翻页后继续拖到目标位置松手；鼠标旁的预览会持续跟随，图标两侧和间隙显示插入位置。分组内的项目可拖到面板外移回主网格；分组与磁盘文件夹分别处理。搜索期间不能整理。Esc依次关闭弹层、文件夹、整理模式、搜索、设置及启动台。移除只删除启动台入口，不会删除原文件或文件夹。

鼠标滚轮可上下翻页；底部分页仅显示当前页短胶囊和其余页淡点，不带底座、不响应鼠标。首页显示LaunchPad标题，不显示项目总数，搜索时保留结果数量。搜索框支持点击放大镜或留白聚焦，边框保持中性灰色，左右方向键用于移动文字光标。输入时点击页面空白处先取消焦点并保留搜索内容，再点击空白处收起窗口。

“＋”按钮点击展开添加菜单，再次点击收起；鼠标离开时取消悬停高亮，不再计时关闭。键盘方向键选择仍显示高亮，点击菜单外或按Esc可关闭菜单。

## 开发与测试

安装 .NET 10 SDK 后运行核心逻辑检查：

```powershell
dotnet run --project .\Launchpad.Core.Tests\Launchpad.Core.Tests.csproj
```

构建桌面程序：

```powershell
dotnet build .\Launchpad\Launchpad.csproj --configuration Release
```

## Windows 便携版

运行 `publish.ps1` 发布 Windows x64 自包含程序。将 `dist\Launchpad-Redesign` 整个文件夹放到合适的位置，双击 `Launchpad.exe` 启动；无需单独安装 .NET 运行时。

托盘图标消失时可重新启动程序；再次启动会唤起已运行的实例。

## 界面验证

`Launchpad.UiTests` 使用独立测试数据，加载指定发布目录中的程序集，检查页面、搜索、图标、原生材质属性、快捷键冲突及带参数快捷方式启动。生成的PNG是运行中的WPF视觉树渲染，不包含DWM桌面模糊，不能用作真实桌面毛玻璃效果的截图证据。

```powershell
dotnet build .\Launchpad.UiTests\Launchpad.UiTests.csproj -c Release
.\Launchpad.UiTests\bin\Release\net10.0-windows\Launchpad.UiTests.exe .\dist\Launchpad-Redesign .\artifacts\ui-redesign
```

`Launchpad.Provision` 是显式执行的一次性桌面应用初始化工具，不会随启动台自动运行。它先输出预览JSON，只有加 `--apply` 才备份并替换应用列表，保留快捷键设置。
