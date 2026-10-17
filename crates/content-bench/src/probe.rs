//! Разбор, что тормозит в системе Luau над столбцами: каждая проба — тот же цикл на 10 тыс., меньше дела.
use mlua::Function;
use std::time::Instant;

pub fn probe(native: bool) -> mlua::Result<()> {
  let lua = content_bench::new_lua(native)?;
  let cases = [
    ("пустой цикл", "for i = 0, n - 1 do end"),
    ("чтение буфера", "local s = 0 for i = 0, n - 1 do s += buffer.readf64(b, i * 8) end"),
    ("чтение и запись", "for i = 0, n - 1 do buffer.writef64(b, i * 8, buffer.readf64(b, i * 8) + 1) end"),
    ("atan2, cos, sin", "local s = 0 for i = 0, n - 1 do s += math.atan2(i, 3) + math.cos(i) + math.sin(i) end"),
    ("таблица вместо буфера", "for i = 1, n do t[i] = t[i] + 1 end"),
  ];
  for (name, body) in cases {
    let source = format!("--!native\nlocal b = buffer.create(80000) local t = table.create(10000, 0) return function(n) {body} end");
    let run: Function = lua.load(source).eval()?;
    for _ in 0..20 {
      run.call::<()>(10_000)?;
    }
    let start = Instant::now();
    for _ in 0..200 {
      run.call::<()>(10_000)?;
    }
    println!("    {name}: {:.3} мс", start.elapsed().as_secs_f64() * 1000.0 / 200.0);
  }
  Ok(())
}
