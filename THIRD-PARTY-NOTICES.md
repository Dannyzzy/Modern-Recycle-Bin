# Third-party notices

This project redistributes the following third-party component. Its license is
reproduced in `lib/WebView2-LICENSE.txt` and summarized below.

## Microsoft.Web.WebView2 SDK

- Files: `lib/Microsoft.Web.WebView2.Core.dll` (`lib/net462` build),
  `lib/WebView2Loader.dll` (`build/native/x64`)
- Source: <https://www.nuget.org/packages/Microsoft.Web.WebView2>
- License: Microsoft Software License Terms for the WebView2 SDK
  (see `lib/WebView2-LICENSE.txt`) — redistribution of the SDK assemblies as part
  of an application is permitted.

The WebView2 *Runtime* itself is not redistributed here. It ships with Windows 11
and with current Windows 10 builds; on machines without it, users install
Microsoft's Evergreen Bootstrapper.

## Not included

- No files from Windows, Explorer, or any third-party application are bundled.
  The Recycle Bin icon is read from the system at runtime
  (`imageres.dll`, via the shell CLSID registration) and is never copied
  into this repository or the distributed package.
