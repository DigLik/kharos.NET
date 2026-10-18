//! Клиент Kharos (docs/content.md, К4): окно, рендер, звук — движок; что рисовать, как звучать, как управлять — моды.
//! Одиночная игра: хост в том же процессе.
//!
//! KHAROS_MODS — папки с модами через `:` (по умолчанию `mods` рядом или выше по дереву), KHAROS_GAME — партия.

pub mod atlas;
mod audio;
mod game;
mod gl;
mod input;
mod render;
mod script;
#[cfg(not(target_arch = "wasm32"))]
mod shell;
mod ui;

use game::Game;
use input::Input;
use kharos_content::{discover, Mod};
use render::Renderer;
use std::path::PathBuf;

/// Шрифт интерфейса — Ark Pixel 12 px (OFL), латиница и кириллица.
const FONT: &[u8] = include_bytes!("../../native/fonts/ark-pixel-12px.otf");

pub struct App {
  renderer: Renderer,
  game: Game,
}

/// Моды: из KHAROS_MODS или папки `mods` в текущей папке и выше.
fn mods() -> Result<Vec<Mod>, String> {
  let roots: Vec<PathBuf> = match std::env::var("KHAROS_MODS") {
    Ok(list) => std::env::split_paths(&list).collect(),
    Err(_) => {
      let mut dir = std::env::current_dir().map_err(|error| error.to_string())?;
      loop {
        if dir.join("mods").is_dir() {
          break vec![dir.join("mods")];
        }
        if !dir.pop() {
          return Err("нет папки mods: запустите из папки игры или задайте KHAROS_MODS".into());
        }
      }
    }
  };
  let mut all = Vec::new();
  for root in roots {
    all.extend(discover(&root).map_err(|error| format!("{}: {error}", root.display()))?);
  }
  Ok(all)
}

impl App {
  pub fn new(gl: glow::Context) -> Result<Self, String> {
    let mut renderer = Renderer::new(gl)?;
    let font = ui::Font::new(FONT)?;
    let game = std::env::var("KHAROS_GAME").ok();
    let mut game = Game::new(mods()?, game.as_deref(), 1, font)?;
    if let Some((pixels, size)) = game.atlas.take() {
      renderer.load_atlas(&pixels, size);
    }
    if let Some(land) = game.land.take() {
      renderer.set_land(land);
    }
    Ok(Self { renderer, game })
  }

  pub fn frame(&mut self, input: &Input, size: [u32; 2]) -> Result<(), String> {
    let (frame, view) = self.game.frame(input)?;
    let size = [size[0] as i32, size[1] as i32];
    self.renderer.draw_world(&view, &frame, size);
    let state = self.game.state.borrow();
    self.renderer.draw_batch(&state.draw, &state.ui.font.atlas, &view, true, self.game.palette());
    self.renderer.draw_batch(&state.ui.batch, &state.ui.font.atlas, &view, false, self.game.palette());
    Ok(())
  }

  pub fn shot(&self, path: &str, size: [u32; 2]) {
    self.renderer.shot(path, size);
  }
}

#[cfg(not(target_arch = "wasm32"))]
pub fn run() {
  shell::run();
}
