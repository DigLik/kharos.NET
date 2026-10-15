//! Привязки того, что в браузере даёт движок, а не страница: пока — перевод текста в байты и обратно.
//!
//! `TextEncoder` и `TextDecoder` — не часть языка JavaScript: их даёт окружение, и у голого V8 их нет. Но сам перевод
//! UTF-8 в строку и обратно V8 умеет, поэтому на JS он здесь не повторяется: операция берёт строку у движка или
//! отдаёт её ему. Дальше сюда попадёт всё, что в браузере делает движок: сжатие, разбор адреса, криптография.

use deno_core::op2;

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

deno_core::extension!(kharos_web, ops = [op_text_decode, op_text_encode]);
