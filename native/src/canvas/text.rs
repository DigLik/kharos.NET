//! Текст холста: `fillText` и `measureText`. Шрифты — те же, что у страницы (`tools/fonts.py`): Ark Pixel и GNU Unifont,
//! только целиком и с диска (`native/fonts`).
//!
//! Семейства — те, что знает CSS страницы (`src/fonts.css`), и с тем же увеличением: у «Kharos Unifont 12» кегль 12
//! даёт настоящие 16 точек Unifont — так он остаётся пиксельным рядом с Ark Pixel 12. Запасной шрифт выбирается по
//! каждой букве, как у браузера: первый в списке, где буква есть. Чего нет нигде, рисует Unifont — он в конце всегда.
//!
//! Глифы собирает `rustybuzz` (вязь, лигатуры, направление письма), контуры даёт `ttf-parser`, а заливает их сам холст
//! (`tiny-skia`): у пиксельного шрифта в родном кегле контуры лежат на сетке, и края выходят ровными.

use std::cell::RefCell;
use std::collections::HashMap;
use std::path::PathBuf;

use rustybuzz::ttf_parser::{GlyphId, OutlineBuilder};
use rustybuzz::{Face, UnicodeBuffer};
use tiny_skia::{Path, PathBuilder};

/// Семейства по имени (без кавычек и регистра): файл шрифта и во сколько раз его кегль больше заданного.
const FAMILIES: [(&str, &str, f32); 4] = [
  ("ark pixel 12", "ark-pixel-12px.otf", 1.0),
  ("ark pixel 10", "ark-pixel-10px.otf", 1.0),
  ("kharos unifont 12", "unifont.otf", 4.0 / 3.0),
  ("kharos unifont 10", "unifont.otf", 1.6),
];
/// Шрифт по умолчанию — для `10px sans-serif` холста и для семейств, которых у окна нет.
const DEFAULT: &str = "ark-pixel-12px.otf";
/// Последний запасной: у него есть почти всё.
const LAST: &str = "unifont.otf";

/// Загруженный шрифт. Живёт до конца программы: шрифтов три, а разбирать их заново на каждый вызов незачем.
struct Font {
  face: Face<'static>,
  units: f32,
}

thread_local! {
  static FONTS: RefCell<HashMap<&'static str, Option<&'static Font>>> = RefCell::new(HashMap::new());
  static SPECS: RefCell<HashMap<String, Spec>> = RefCell::new(HashMap::new());
}

/// Где лежат шрифты: `KHAROS_FONTS`, иначе `fonts` рядом с окном (туда их кладёт `tools/fonts.py`).
fn folder() -> PathBuf {
  std::env::var_os("KHAROS_FONTS").map(PathBuf::from).unwrap_or_else(|| PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("fonts"))
}

/// Шрифт по имени файла; нет файла — `None`, и в журнале один раз сказано, где его взять.
fn font(file: &'static str) -> Option<&'static Font> {
  FONTS.with(|fonts| {
    *fonts.borrow_mut().entry(file).or_insert_with(|| {
      let path = folder().join(file);
      let data = match std::fs::read(&path) {
        Ok(data) => data,
        Err(error) => {
          eprintln!("шрифт {} не прочитан ({error}): собрать шрифты — python3 tools/fonts.py", path.display());
          return None;
        }
      };
      let data: &'static [u8] = Box::leak(data.into_boxed_slice());
      let face = Face::from_slice(data, 0)?;
      let units = face.units_per_em() as f32;
      Some(&*Box::leak(Box::new(Font { face, units })))
    })
  })
}

/// Разобранная запись `font`: кегль и шрифты по порядку — с их увеличением.
#[derive(Clone)]
pub struct Spec {
  size: f32,
  chain: Vec<(&'static Font, f32)>,
}

/// Разбирает запись шрифта CSS: `[стиль] [толщина] 12px[/высота] "Семейство", запасное`. Наклон и толщину пиксельные
/// шрифты не умеют — их нет и у страницы, — так что берутся только кегль и семейства.
pub fn spec(font_value: &str) -> Spec {
  if let Some(spec) = SPECS.with(|specs| specs.borrow().get(font_value).cloned()) {
    return spec;
  }
  let mut size = 10.0;
  let mut families = "";
  // Слова до кегля — стиль и толщина, после него — семейства (имена с пробелами идут как есть).
  let mut rest = font_value;
  loop {
    let trimmed = rest.trim_start();
    let end = trimmed.find(char::is_whitespace).unwrap_or(trimmed.len());
    let word = &trimmed[..end];
    rest = &trimmed[end..];
    if word.is_empty() {
      break;
    }
    if let Some(number) = word.split('/').next().and_then(|head| head.strip_suffix("px")).and_then(|value| value.parse::<f32>().ok()) {
      size = number;
      families = rest;
      break;
    }
  }
  let mut chain = Vec::new();
  for name in families.split(',') {
    let name = name.trim().trim_matches(|c| c == '"' || c == '\'').to_lowercase();
    if let Some((_, file, scale)) = FAMILIES.iter().find(|(family, _, _)| *family == name) {
      if let Some(font) = font(file) {
        chain.push((font, *scale));
      }
    }
  }
  if chain.is_empty() {
    chain.extend(font(DEFAULT).map(|font| (font, 1.0)));
  }
  if !chain.iter().any(|(font, _)| std::ptr::eq(*font, font_of(LAST))) {
    chain.extend(font(LAST).map(|font| (font, 4.0 / 3.0)));
  }
  let spec = Spec { size, chain };
  SPECS.with(|specs| specs.borrow_mut().insert(font_value.to_string(), spec.clone()));
  spec
}

/// Указатель шрифта для сравнения (без шрифта — пустой: ни с чем не совпадёт).
fn font_of(file: &'static str) -> *const Font {
  font(file).map_or(std::ptr::null(), |font| font as *const Font)
}

/// Глиф на месте: шрифт, номер, точка письма в пикселях от начала строки и масштаб «пикселей на единицу шрифта».
struct Placed {
  font: &'static Font,
  glyph: GlyphId,
  x: f32,
  y: f32,
  scale: f32,
}

/// Разложенная строка: глифы, ширина и границы первого шрифта — по ним ставится базовая линия, как у браузера.
pub struct Line {
  glyphs: Vec<Placed>,
  pub width: f32,
  pub ascent: f32,
  pub descent: f32,
}

/// Раскладывает строку: делит её на куски по шрифтам (первый, где буква есть) и собирает глифы каждого куска.
pub fn lay(spec: &Spec, text: &str) -> Line {
  let Some(&(first, first_scale)) = spec.chain.first() else {
    return Line { glyphs: Vec::new(), width: 0.0, ascent: 0.0, descent: 0.0 };
  };
  let pixels = |font: &Font, scale: f32| spec.size * scale / font.units;
  let ascent = first.face.ascender() as f32 * pixels(first, first_scale);
  let descent = -(first.face.descender() as f32) * pixels(first, first_scale);
  // Куски: пробелы и знаки без глифа не рвут кусок, а идут в тот, где стоят.
  let mut runs: Vec<(usize, String)> = Vec::new();
  for char in text.chars() {
    let found = spec.chain.iter().position(|(font, _)| font.face.glyph_index(char).is_some());
    let index = match (found, runs.last()) {
      (_, Some((last, _))) if char.is_whitespace() => *last,
      (Some(index), _) => index,
      (None, _) => spec.chain.len() - 1,
    };
    match runs.last_mut() {
      Some((last, run)) if *last == index => run.push(char),
      _ => runs.push((index, char.to_string())),
    }
  }
  let mut glyphs = Vec::new();
  let mut pen = 0.0;
  for (index, run) in runs {
    let (font, scale) = spec.chain[index];
    let unit = pixels(font, scale);
    let mut buffer = UnicodeBuffer::new();
    buffer.push_str(&run);
    buffer.guess_segment_properties();
    let shaped = rustybuzz::shape(&font.face, &[], buffer);
    for (info, position) in shaped.glyph_infos().iter().zip(shaped.glyph_positions()) {
      glyphs.push(Placed {
        font,
        glyph: GlyphId(info.glyph_id as u16),
        x: pen + position.x_offset as f32 * unit,
        y: -(position.y_offset as f32) * unit,
        scale: unit,
      });
      pen += position.x_advance as f32 * unit;
    }
  }
  Line { glyphs, width: pen, ascent, descent }
}

/// Контур глифа в путь холста: шрифт рисует вверх от базовой линии, холст — вниз, отсюда минус у y.
struct Outline<'a> {
  builder: &'a mut PathBuilder,
  x: f32,
  y: f32,
  scale: f32,
}

impl Outline<'_> {
  fn at(&self, x: f32, y: f32) -> (f32, f32) {
    (self.x + x * self.scale, self.y - y * self.scale)
  }
}

impl OutlineBuilder for Outline<'_> {
  fn move_to(&mut self, x: f32, y: f32) {
    let (x, y) = self.at(x, y);
    self.builder.move_to(x, y);
  }
  fn line_to(&mut self, x: f32, y: f32) {
    let (x, y) = self.at(x, y);
    self.builder.line_to(x, y);
  }
  fn quad_to(&mut self, x1: f32, y1: f32, x: f32, y: f32) {
    let ((x1, y1), (x, y)) = (self.at(x1, y1), self.at(x, y));
    self.builder.quad_to(x1, y1, x, y);
  }
  fn curve_to(&mut self, x1: f32, y1: f32, x2: f32, y2: f32, x: f32, y: f32) {
    let ((x1, y1), (x2, y2), (x, y)) = (self.at(x1, y1), self.at(x2, y2), self.at(x, y));
    self.builder.cubic_to(x1, y1, x2, y2, x, y);
  }
  fn close(&mut self) {
    self.builder.close();
  }
}

/// Путь всей строки от точки письма (x, y) на базовой линии. Точка округляется до пикселя: пиксельный шрифт на
/// дробной точке размылся бы.
pub fn outline(line: &Line, x: f32, y: f32) -> Option<Path> {
  let mut builder = PathBuilder::new();
  let (x, y) = (x.round(), y.round());
  for placed in &line.glyphs {
    let mut outline = Outline { builder: &mut builder, x: x + placed.x, y: y + placed.y, scale: placed.scale };
    placed.font.face.outline_glyph(placed.glyph, &mut outline);
  }
  builder.finish()
}
