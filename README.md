# PhoneCam: use your iPhone as a USB webcam on Windows

PhoneCam has two parts that talk to each other over the USB-C cable:

```
 iPhone app (Swift)                         Windows app (C#, .NET 8)
 ─────────────────────                      ──────────────────────────────────────────
 AVCapture front/back camera                Apple Mobile Device Service (usbmux, :27015)
   → JPEG frames                              → tunnel to the phone's port 5858
   → TCP server on 127.0.0.1:5858  ═══USB═══→ decode, rotate/mirror/crop
     (loopback only: not on Wi-Fi)            → softcam virtual camera "DirectShow Softcam"
                                              → Zoom, Teams, OBS, Chrome, Edge…
```

You can switch between the front and back cameras on the phone or in the Windows app. Each app keeps the other in sync.

---

## 1. What you need

| Where | What |
|---|---|
| Windows | **Apple Devices** app (Microsoft Store) **or** iTunes. Either one installs *Apple Mobile Device Service*, which provides the USB connection |
| Windows | **.NET 8 SDK** (or Visual Studio 2022 with the ".NET desktop development" workload) |
| Windows | **Visual Studio 2022** with "Desktop development with C++", to build softcam once |
| Mac | **Xcode 15+**, to build and install the iPhone app. An iOS app can only be built on macOS |
| iPhone | iOS 16 or newer, with Developer Mode turned on (Settings › Privacy & Security) |

> No Mac? Your options are a cloud Mac (MacinCloud, AWS EC2 Mac) or a GitHub Actions macOS runner with your signing certificate. With a **free** Apple ID, the app you install expires after 7 days and has to be reinstalled. A paid developer account ($99/yr) gives 1 year, or TestFlight.

---

## 2. Install the virtual camera driver (softcam), once

PhoneCam uses [softcam](https://github.com/tshino/softcam) (MIT licence), a DirectShow virtual camera that an app can feed frames into.

softcam v1.8.1 is already built (x64 Release) in **`windows/softcam/`**. To install it, run:

```powershell
powershell -ExecutionPolicy Bypass -File windows\softcam\Install-Softcam.ps1
```

Click **Yes** at the administrator prompt. The script copies the files to **`C:\Program Files\Softcam\`** and registers the DLL there. Registration saves the DLL's full path, so don't move it afterwards. A camera called **"DirectShow Softcam"** now exists on your PC. To remove it, run the same script with `-Uninstall`.

To rebuild softcam yourself (softcam publishes no prebuilt binaries): clone `https://github.com/tshino/softcam`, then use MSBuild from the VS 2022 C++ Build Tools to build target `softcam` of `softcam.sln` and `examples/softcam_installer/softcam_installer.sln`, both as **Release | x64**.

The Windows app looks for `softcam.dll` in this order: the `SOFTCAM_DLL` environment variable, then the folder next to `PhoneCamDesktop.exe`, then `C:\Program Files\Softcam\softcam.dll`. A copy of `softcam.dll` placed in `windows/PhoneCamDesktop/` is also copied to the build output automatically.

The Windows app is published to `windows/PhoneCamDesktop/publish/PhoneCamDesktop.exe`.

---

## 3. Build and run the Windows app

```powershell
cd windows\PhoneCamDesktop
dotnet run -c Release
```

You can also open `PhoneCamDesktop.csproj` in Visual Studio and press F5. To get a single folder you can copy anywhere:

```powershell
dotnet publish -c Release -o publish
```

---

## 4. Build and install the iPhone app (on a Mac)

```bash
brew install xcodegen
cd ios
xcodegen generate          # creates PhoneCam.xcodeproj from project.yml
open PhoneCam.xcodeproj
```

In Xcode, go to *Signing & Capabilities* and pick your Team. Change the bundle id `com.example.phonecam` to your own. Then select your iPhone and press **Run**.

*(If you'd rather not use XcodeGen: create a new iOS App project in Xcode (SwiftUI, Swift), delete the template files, drag in the 5 files from `ios/PhoneCam/`, and add the Info.plist key `NSCameraUsageDescription`.)*

---

## 5. Use it

1. Connect the iPhone to the laptop with the USB-C cable. Unlock it and tap **Trust** if asked.
2. Open **PhoneCam** on the iPhone and allow camera access.
3. Start **PhoneCamDesktop** on Windows. Within about a second it shows *Connected to iPhone* and the preview.
4. In Zoom, Teams, OBS, Discord, Chrome or Edge, choose the camera **"DirectShow Softcam"**.
5. Choose **Front / Back camera** on either device. In the Windows app, use **Rotation**, **Mirror** and **Framing** to fit how the phone is mounted.

Tips:
- **Auto-rotate** is on by default (on the phone and as *Follow phone rotation* in the Windows app). The phone reads its motion sensors (gyroscope and accelerometer) and turns the picture so you stay upright, whichever way it's held. The virtual webcam always stays 16:9 landscape: a portrait-held phone is cropped to the middle band (*Fill frame*) or shown with black side bars (*Fit whole image*). This works even with Portrait Orientation Lock on. When the phone lies flat, it keeps the last orientation.
- Use **Extra rotation** only if the picture is still wrong, or when auto-rotate is off (then 0° suits a landscape mount with the back camera).
- Leave **Mirror** off. Video-call apps mirror your self-view themselves, and other people see you the right way round.
- The phone app has to stay **open and in the foreground**, because iOS stops the camera in the background. The screen stays on while streaming, and the phone charges over the same cable.

---

## Protocol (if you want to extend it)

Each message, in either direction, is `[UInt32 big-endian payload length][UInt8 type][payload]`:

| Type | Direction | Payload |
|---|---|---|
| `0x01` | phone → PC | JPEG frame, unmirrored. Upright when auto-rotate is on (portrait-shaped when the phone is held upright); native sensor orientation when it's off |
| `0x02` | phone → PC | JSON status: `{"camera":"back","resolution":"720p","quality":0.6,"cameraAuthorized":true,"autoRotate":true,"orientation":"portrait","error":""}`. `orientation` is one of `portrait`, `portraitUpsideDown`, `landscapeLeft` (top edge pointing left), `landscapeRight` |
| `0x10` | PC → phone | JSON command, any of: `{"camera":"front"\|"back"}`, `{"resolution":"720p"\|"1080p"}`, `{"quality":0.1–1.0}`, `{"autoRotate":true\|false}`, `{"requestStatus":true}` |

The PC reaches the phone through usbmux's `Connect` request (plist protocol v1 on `127.0.0.1:27015`, port in network byte order). This is the same mechanism Xcode and iTunes use. The phone drops frames instead of queuing them when the PC falls behind, which keeps latency low.

## Known limitations / ideas

- **DirectShow only.** softcam works with apps that use DirectShow, which covers most video-call apps, OBS and Chromium browsers. Apps that use only Media Foundation, such as the built-in Windows *Camera* app, won't see it. To support those, write a Windows 11 Media Foundation virtual camera (`MFCreateVirtualCamera`) as a further step.
- The camera name "DirectShow Softcam" is set inside softcam. To show "PhoneCam" instead, change the name in softcam's source and rebuild.
- MJPEG is simple and low-latency. For 1080p at 60 fps, switch to H.264 using VideoToolbox on the phone and Media Foundation decoding on Windows.
- The app connects to the first iPhone it finds on USB.
- Possible additions: zoom/lens selection (ultra-wide, telephoto), focus/exposure lock, using the phone's microphone as an audio source.
