// 只引入与 WPF / WinForms 都不冲突的基础命名空间。
// WPF、WinForms、Drawing 在各文件里按需用全名或别名引入，避免类型二义性。
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
// HID 协议层与 WLMouseTray 共用同一份源码，命名空间也保持一致
global using WLMouseTray;
