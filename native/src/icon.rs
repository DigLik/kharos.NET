//! Значок окна — тот же, что у вкладки страницы (`apps/web/public/favicon.png`), вшитый в окно: без него система ставит свой.
//! На Windows и в Linux значок задаётся окну (`with_window_icon`); на macOS окну значка нет — его показывает Док, и
//! у окна без пакета приложения (`.app`) его ставит само приложение через AppKit.

use winit::window::Icon;

const PNG: &[u8] = include_bytes!("../../apps/web/public/favicon.png");

/// Значок для окна: пиксели без умножения на альфу, как их ждёт winit. Не разобрался — окно идёт со значком системы.
pub fn window_icon() -> Option<Icon> {
  let image = skia_safe::Image::from_encoded(skia_safe::Data::new_copy(PNG))?;
  let (width, height) = (image.width(), image.height());
  let info = skia_safe::ImageInfo::new((width, height), skia_safe::ColorType::RGBA8888, skia_safe::AlphaType::Premul, None);
  let mut surface = skia_safe::surfaces::raster(&info, None, None)?;
  surface.canvas().draw_image(&image, (0.0, 0.0), None);
  let pixmap = surface.peek_pixels()?;
  let stored = pixmap.bytes()?;
  let row_bytes = pixmap.row_bytes();
  let mut rgba = Vec::with_capacity((width * height * 4) as usize);
  for y in 0..height as usize {
    for x in 0..width as usize {
      let at = y * row_bytes + x * 4;
      let [r, g, b, a] = [stored[at], stored[at + 1], stored[at + 2], stored[at + 3]];
      // Обратное умножение на альфу — та же арифметика, что у холста в `get_image_data`.
      let divide = |c: u8| if a == 0 { 0 } else { ((c as u32 * 255 + a as u32 / 2) / a as u32).min(255) as u8 };
      rgba.extend_from_slice(&[divide(r), divide(g), divide(b), a]);
    }
  }
  Icon::from_rgba(rgba, width as u32, height as u32).ok()
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
