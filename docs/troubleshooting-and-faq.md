# Troubleshooting & Diagnostic Guide

## 1. Black Screen on Virtual Display

### Symptoms:
The client connects, but the canvas remains black or solid dark gray.

### Root Causes & Fixes:
1. **Empty Windows Desktop**:
   - Newly instantiated Windows virtual monitors do not have desktop icons, taskbars, or wallpaper assigned by Windows.
   - *Fix*: Drag any open window (e.g. Chrome, Notepad, Terminal) across the edge of your physical screen onto the iPad screen. It will immediately appear.
2. **DXGI Desktop Duplication Access Denied**:
   - `IDXGIOutputDuplication` returns `E_ACCESSDENIED (0x80070005)` if the process is un-elevated.
   - *Fix*: Run the service via [`run_service_admin.bat`](../run_service_admin.bat) (Run as Administrator) or click **🛡️ Start Elevated** in the Console.
3. **Display Mode in Disconnected State**:
   - In `OpenWinSidecar.Console`, click **⚡ 3rd Screen ON** or press `Win + P` and select **Extend**.

---

## 2. Low FPS / Stutter

### Root Causes & Fixes:
1. **GDI Fallback vs DXGI**:
   - GDI `BitBlt` captures at 15–25 FPS due to CPU memory transfers.
   - DXGI captures directly in GPU VRAM at 60–120 FPS. Ensure the service runs as Administrator.
2. **Wi-Fi Packet Loss**:
   - Switch from 2.4 GHz Wi-Fi to 5 GHz / Wi-Fi 6, or plug in a USB cable and connect via Apple USB tethering.

---

## 3. Double Mouse Pointers

### Symptoms:
You see two mouse cursors moving on top of each other on your iPad/client browser.

### Root Causes & Fixes:
- **Dual Rendering**: The host capture engine draws the Windows cursor into the video frame, while the browser draws its own CSS cursor over the canvas.
- **Fix**: OpenWinSidecar automatically applies `cursor: none !important` to the web client. If you customized styles, ensure the client cursor is hidden or select **🖥️ Host Windows Cursor** in the web settings modal.

---

## 4. Disabling the 3rd Screen & Windows Recovery

### Question:
*How do I turn off the 3rd screen so my cursor and windows don't get lost in an invisible monitor?*

### Answer:
- In `OpenWinSidecar.Console`, click **🔌 3rd Screen OFF** (or via CLI: `openwinsidecar screen off`).
- This disables the device node (`ROOT\DISPLAY\0000`), causing Windows to immediately snap all open application windows back to your primary display and stop the streaming service.

---

## 5. Virtual Display Driver Reset

If the virtual monitor stops responding:
1. Open `OpenWinSidecar.Console`.
2. Click **🔄 Restart Driver & Stream** (or via CLI: `openwinsidecar driver restart`).
3. The system executes `pnputil.exe /restart-device "ROOT\DISPLAY\0000"` and calls `SetDisplayConfig` to re-extend all displays.

---

## 6. Complete Shutdown (Service + Driver)

To completely stop all background services, kill orphaned FFmpeg workers, and disable the virtual display driver:
- In Console: Click **🛑 Shutdown All** in the header or **🛑 Complete Shutdown (Service + Driver)** in Tab 1 / Tab 2.
- In CLI: Run `openwinsidecar shutdown`.
- In System Tray: Right-click tray icon and select **🛑 Complete Shutdown (All)**.
