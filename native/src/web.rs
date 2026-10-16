//! Привязки того, что в браузере даёт движок, а не страница: перевод текста в байты и обратно, сжатие и часы.
//!
//! `TextEncoder` и `TextDecoder` — не часть языка JavaScript: их даёт окружение, и у голого V8 их нет. Но сам перевод
//! UTF-8 в строку и обратно V8 умеет, поэтому на JS он здесь не повторяется: операция берёт строку у движка или
//! отдаёт её ему. Дальше сюда попадёт всё, что в браузере делает движок: сжатие, разбор адреса, криптография.

use std::io::Write;
use std::sync::OnceLock;
use std::time::Instant;

use deno_core::op2;
use deno_error::JsErrorBox;
use flate2::write::{DeflateDecoder, DeflateEncoder, GzDecoder, GzEncoder, ZlibDecoder, ZlibEncoder};
use flate2::Compression;

/// Байты UTF-8 в строку. Испорченные байты заменяются знаком замены — как у браузера без строгой расшифровки.
#[op2]
#[string]
pub fn op_text_decode(#[buffer] bytes: &[u8]) -> String {
  String::from_utf8_lossy(bytes).into_owned()
}

/// Строка в байты UTF-8.
#[op2]
#[buffer]
pub fn op_text_encode(#[string] text: &str) -> Vec<u8> {
  text.as_bytes().to_vec()
}

/// Сжатие и разжатие целиком, как его видят `CompressionStream` и `DecompressionStream` у страницы: поток там лишь
/// обёртка (см. `js/prelude.js`), а работа — здесь. Виды — те же, что у страницы: `deflate-raw` (им хост сжимает
/// карту, а сохранение — тело файла), `deflate` (zlib) и `gzip`.
#[op2]
#[buffer]
pub fn op_compress(#[buffer] bytes: &[u8], #[string] format: &str, inflate: bool) -> Result<Vec<u8>, JsErrorBox> {
  let mut out = Vec::new();
  let level = Compression::default();
  // Весь кусок разом: записать и дописать хвост потока (у сжатия — последний блок и сумму, у разжатия — остаток).
  macro_rules! whole {
    ($coder:expr) => {{
      let mut coder = $coder;
      coder.write_all(bytes).and_then(|_| coder.try_finish())
    }};
  }
  let done = match (format, inflate) {
    ("deflate-raw", false) => whole!(DeflateEncoder::new(&mut out, level)),
    ("deflate-raw", true) => whole!(DeflateDecoder::new(&mut out)),
    ("deflate", false) => whole!(ZlibEncoder::new(&mut out, level)),
    ("deflate", true) => whole!(ZlibDecoder::new(&mut out)),
    ("gzip", false) => whole!(GzEncoder::new(&mut out, level)),
    ("gzip", true) => whole!(GzDecoder::new(&mut out)),
    _ => return Err(JsErrorBox::type_error(format!("неизвестный вид сжатия: {format}"))),
  };
  done.map_err(|error| JsErrorBox::type_error(format!("{format}: {error}")))?;
  Ok(out)
}

/// Начало часов окна: от него идут и `performance.now()`, и время кадров (см. `src/pace.rs`) — одни часы на обоих.
static START: OnceLock<Instant> = OnceLock::new();

/// Миллисекунды с начала часов окна — монотонные и точнее миллисекунды, как `performance.now()` у страницы.
pub fn now_ms() -> f64 {
  START.get_or_init(Instant::now).elapsed().as_secs_f64() * 1000.0
}

/// `performance.now()`.
#[op2(fast)]
pub fn op_time_now() -> f64 {
  now_ms()
}

deno_core::extension!(kharos_web, ops = [op_text_decode, op_text_encode, op_compress, op_time_now]);
