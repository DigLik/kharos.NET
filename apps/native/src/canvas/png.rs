//! PNG и base64 для `toDataURL`: скины и значки идут в стили, портреты — в `<img>`, и всё это строкой.
//!
//! Сжатие — «как есть»: полосы ложатся в поток без упаковки. Картинки здесь крошечные (скин 12×12, значок 8×8,
//! портрет 32×32), и упаковка отняла бы больше, чем дала. Пиксели — как в `ImageData`: цвет не умножен на альфу.

/// Картинка в PNG: восемь бит на канал, цвет с альфой, без черезстрочности — как отдаёт холст браузера.
pub fn png(width: usize, height: usize, pixels: &[u8]) -> Vec<u8> {
  let mut out = vec![0x89, b'P', b'N', b'G', 0x0d, 0x0a, 0x1a, 0x0a];
  let mut header = Vec::new();
  header.extend_from_slice(&(width as u32).to_be_bytes());
  header.extend_from_slice(&(height as u32).to_be_bytes());
  header.extend_from_slice(&[8, 6, 0, 0, 0]);
  chunk(&mut out, b"IHDR", &header);
  let mut raw = Vec::with_capacity(height * (width * 4 + 1));
  for row in 0..height {
    // Перед полосой — её фильтр; нулевой значит «без преобразования».
    raw.push(0);
    raw.extend_from_slice(&pixels[row * width * 4..(row + 1) * width * 4]);
  }
  chunk(&mut out, b"IDAT", &zlib(&raw));
  chunk(&mut out, b"IEND", &[]);
  out
}

/// Кусок PNG: длина, вид, данные и контрольная сумма — как требует формат.
fn chunk(out: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
  out.extend_from_slice(&(data.len() as u32).to_be_bytes());
  let start = out.len();
  out.extend_from_slice(kind);
  out.extend_from_slice(data);
  let sum = crc32(&out[start..]);
  out.extend_from_slice(&sum.to_be_bytes());
}

/// Поток zlib без упаковки: заголовок, полосы «как есть» и контрольная сумма потока.
fn zlib(raw: &[u8]) -> Vec<u8> {
  let mut out = vec![0x78, 0x01];
  let mut rest = raw;
  loop {
    let take = rest.len().min(0xffff);
    let last = rest.len() <= 0xffff;
    out.push(if last { 1 } else { 0 });
    out.extend_from_slice(&(take as u16).to_le_bytes());
    out.extend_from_slice(&(!(take as u16)).to_le_bytes());
    out.extend_from_slice(&rest[..take]);
    rest = &rest[take..];
    if last {
      break;
    }
  }
  out.extend_from_slice(&adler32(raw).to_be_bytes());
  out
}

fn adler32(data: &[u8]) -> u32 {
  const MOD: u32 = 65521;
  let (mut low, mut high) = (1u32, 0u32);
  for byte in data {
    low = (low + *byte as u32) % MOD;
    high = (high + low) % MOD;
  }
  (high << 16) | low
}

fn crc32(data: &[u8]) -> u32 {
  let mut crc = 0xffff_ffffu32;
  for byte in data {
    crc ^= *byte as u32;
    for _ in 0..8 {
      crc = if crc & 1 != 0 { (crc >> 1) ^ 0xedb8_8320 } else { crc >> 1 };
    }
  }
  !crc
}

/// base64: `toDataURL` отдаёт картинку текстом, а этим её читают и стили, и `<img>`.
pub fn base64(data: &[u8]) -> String {
  const ALPHABET: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  let mut out = String::with_capacity(data.len().div_ceil(3) * 4);
  for part in data.chunks(3) {
    let packed = (part[0] as u32) << 16 | (*part.get(1).unwrap_or(&0) as u32) << 8 | *part.get(2).unwrap_or(&0) as u32;
    for shift in [18, 12, 6, 0] {
      out.push(ALPHABET[(packed >> shift & 0x3f) as usize] as char);
    }
    // Хвост короче трёх байт добирается знаками «=» — так его читает и браузер.
    if part.len() < 3 {
      out.pop();
      out.push('=');
    }
    if part.len() < 2 {
      out.pop();
      out.push('=');
    }
  }
  out
}
