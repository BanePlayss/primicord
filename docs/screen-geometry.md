# Screen-share tile geometry correction

Release target: Primicord 1.0.0. Updating the source or publishing a release does
not replace an already installed application automatically.

## Cause

The receiver assembled encoded tiles with `Graphics.DrawImageUnscaled`. Despite
the name, this API uses physical image dimensions and source/destination DPI.
A 128-pixel tile carrying 144-DPI metadata rendered about 85 pixels wide on a
96-DPI canvas, while the next tile still started 128 pixels away. This reproduced
the regular black gaps reported by the user.

Reference: [Microsoft DrawImageUnscaled documentation](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.graphics.drawimageunscaled?view=windowsdesktop-10.0).

## Correction

`ScreenReceiver.OnUpdate` now uses explicit source and destination pixel
rectangles, a 96-DPI canvas with pixel units, and pixel-preserving drawing
settings. Decoded tile dimensions must match their protocol position, including
partial edge tiles. No wire-format, compression, sender, or version changes.
Updated receivers accept existing senders with either PNG or JPEG metadata.
Unchanged receivers still require the update to receive this fix.

## Regression verification

Run:

```powershell
dotnet run --project Tests/ScreenGeometry/ScreenGeometry.csproj -c Release -- --full
```

Before correction, the synthetic fixture failed at pixel (85, 0) with 144-DPI
PNG input: expected 255, actual 0. After correction, all 96 cases passed:

- Resolutions: 262x134, 1280x720, 1366x768, 1920x1080, 2560x1440,
  3440x1440, 1080x1920, 3840x2160.
- Image DPI metadata: 72, 96, 120, 144, 192, 300.
- Encodings: PNG and JPEG.
- Every decoded pixel compared, including partial right/bottom tiles and
  resolution changes for the same sender.

Fixtures use synthetic images, not desktop capture or live calls. JPEG checks
preservation of decoded pixels, not lossless compression. This test does not
claim a minimum network frame rate or validate live monitor-DPI transitions.
The previously prepared typography changes remain intact.
