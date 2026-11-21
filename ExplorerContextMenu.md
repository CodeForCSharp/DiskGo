# 资源管理器右键菜单

状态：未做。扫描引擎不依赖它，做之前先把核心扫描做完。

目标：在资源管理器里对一个盘、一个文件夹、或文件夹的空白处右键，能直接用该路径启动 DiskGo 并开始扫描。要同时覆盖经典右键菜单和 Windows 11 的新菜单。

## 行为

- 菜单项出现在三种位置：驱动器、文件夹、文件夹背景。文件上不出现。
- 点击后启动主程序，把被点的路径当作扫描目标传过去。文件夹背景传的是当前文件夹。
- 主程序已经能从命令行接收路径（见 `Program.cs`），菜单不需要另外一套扫描入口。它只负责把路径交给现有的启动参数。
- 提供注册和卸载。卸载要把两套菜单都清掉，不留残留的注册表项和包。

## 经典右键菜单

用注册表静态动词，不需要进程内的处理程序。写入时需要管理员权限，因为要改 `HKLM`。

- 驱动器：`HKLM\Software\Classes\Drive\shell\DiskGo`
- 文件夹：`HKLM\Software\Classes\Directory\shell\DiskGo`
- 文件夹背景：`HKLM\Software\Classes\Directory\Background\shell\DiskGo`

每个键下放：

- 默认值：菜单上显示的文字，例如「用 DiskGo 分析」。
- `Icon`：指向主程序的图标。
- `command` 子键：`"<u4e3b程序完整路径>" "%1"`。背景菜单用 `%V`，`%1` 在背景上是空的。

路径里的空格必须用引号括起来。卸载时删掉这三个键。

## Windows 11 新菜单

Windows 11 的新菜单不读上面那套 `shell` 键。它要求一个有包标识的 COM 服务器，并且只能通过稀疏包注册。经典菜单留着：旧系统、以及新菜单里的「显示更多选项」，走的都是它。

做法：

1. 加一个独立的原生 COM DLL，不要放进扫描引擎。它被资源管理器加载，崩溃会拖坏资源管理器。
2. 实现 `IExplorerCommand`。`GetTitle` 返回菜单文字，`GetIcon` 返回图标，`GetState` 对文件返回隐藏，`Invoke` 里用 `ShellExecute` 启动主程序并传入选中项的路径。多选时只取第一个路径，或者把全部传给已有的多路径扫描。
3. 导出 `DllGetClassObject` 和 `DllCanUnloadNow`，线程模型用 STA。给这个类一个固定的 CLSID，一旦发布就不要再改。
4. 做一个稀疏的 MSIX。清单里不放程序文件，用 `uap10:AllowExternalContent`，让它引用安装目录里已有的 DLL 和主程序。
5. 清单里声明：
   - `desktop4:Extension`，类别 `windows.fileExplorerContextMenus`。
   - 三个 `ItemType`：`Directory`、`Directory\Background`、`Drive`，每个都指向同一个 CLSID。`Drive` 需要 `desktop10` 命名空间。
   - `com:Extension`，类别 `windows.comServer`，用 `SurrogateServer` 挂上这个 CLSID，`Path` 指向 DLL，`ThreadingModel` 为 `STA`。
   - 应用的 `Executable` 指向主程序，`uap:VisualElements` 的 `AppListEntry` 设为 `none`，不让它出现在开始菜单里。
   - 声明 `runFullTrust`。
6. 用自有证书签名。清单里的 `Publisher` 必须和证书主体逐字一致。
7. 注册用 `Add-AppxPackage -ExternalLocation <安装目录>`，让包指向真正放着 DLL 和主程序的目录。卸载用 `Remove-AppxPackage` 按包名移除。

注册之后资源管理器不一定立刻刷新菜单，需要重启资源管理器才能看到。这是系统行为，不是注册失败。

## 安装与卸载

- 安装时先写三个经典菜单键，再注册稀疏包。两步都需要提权。
- 卸载时先移除稀疏包，再删注册表键。顺序倒过来，避免卸一半时菜单还指向已删除的文件。
- 主程序提供一个显式的卸载入口，例如命令行开关，不要只能靠手动改注册表。
- DLL 和主程序必须在同一个目录。稀疏包按相对路径引用它们，安装目录一旦搬走，菜单就失效。

## 不要做的事

- 不要在 DLL 里做任何扫描、读取 MFT 或遍历目录。它只负责把路径交给主程序。
- 不要把菜单挂到文件上。扫描的对象是盘和文件夹。
- 不要用进程内注入去改资源管理器。两套官方机制已经够用。
