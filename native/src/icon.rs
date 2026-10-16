//! Значок окна — тот же, что у вкладки страницы (`public/favicon.png`), вшитый в окно: без него система ставит свой.
//! На Windows и в Linux значок задаётся окну (`with_window_icon`); на macOS окну значка нет — его показывает Док, и
//! у окна без пакета приложения (`.app`) его ставит само приложение через AppKit.

use winit::window::Icon;

const PNG: &[u8] = include_bytes!("../../public/favicon.png");

/// Значок для окна: пиксели без умножения на альфу, как их ждёт winit. Не разобрался — окно идёт со значком системы.
pub fn window_icon() -> Option<Icon> {
  let pixmap = tiny_skia::Pixmap::decode_png(PNG).ok()?;
  let rgba = pixmap.pixels().iter().flat_map(|pixel| {
    let color = pixel.demultiply();
    [color.red(), color.green(), color.blue(), color.alpha()]
  });
  Icon::from_rgba(rgba.collect(), pixmap.width(), pixmap.height()).ok()
}

/// Значок в Доке macOS: ставить из главного потока, когда приложение уже заведено (после создания окна).
#[cfg(target_os = "macos")]
pub fn dock_icon() {
  use objc2::ClassType;
  use objc2_app_kit::{NSApplication, NSImage};
  use objc2_foundation::{MainThreadMarker, NSData};
  let Some(main) = MainThreadMarker::new() else { return };
  let data = NSData::with_bytes(PNG);
  let Some(image) = NSImage::initWithData(NSImage::alloc(), &data) else { return };
  unsafe { NSApplication::sharedApplication(main).setApplicationIconImage(Some(&image)) };
}

#[cfg(not(target_os = "macos"))]
pub fn dock_icon() {}
