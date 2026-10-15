//! Рантайм JavaScript: игра идёт на нём как есть, без переписывания.
//!
//! Рантайм один на окно и живёт на главном потоке: так привязки GL дотягиваются до него без пересылки данных.
//! Кадры игра получает так же, как от браузера: Rust зовёт `__kharos_frame`, а очередь кадров ведёт обёртка.

use std::rc::Rc;

use anyhow::Result;
use deno_core::{JsRuntime, ModuleSpecifier, RuntimeOptions};

/// Обёртка браузерного API (`js/prelude.js`): то, что в браузере даёт страница, — холст, кадры, таймеры, `console`
/// и состояние WebGL. Файл кладётся в двоичный файл: рядом с ним на диске ничего не нужно.
const PRELUDE: &str = include_str!("../js/prelude.js");

pub struct Engine {
  runtime: JsRuntime,
  /// Токийный контекст: deno_core берёт из него способ отложить работу V8, а без него рантайм себя же и останавливает
  /// (V8 просит отложенную работу у сборщика мусора). Живёт столько же, сколько рантайм, и значений не держит.
  _tokio: tokio::runtime::Runtime,
}

impl Engine {
  /// Поднимает рантайм с привязками: GL и того, что в браузере даёт движок. Обёртка браузерного API уходит в
  /// рантайм вместе с кодом игры (см. `run`).
  pub fn new() -> Result<Self> {
    // Поток один: вся работа идёт на главном, а от контекста нужна только задержка во времени.
    let tokio = tokio::runtime::Builder::new_current_thread().enable_time().build()?;
    let runtime = {
      // Контекст нужен именно здесь: deno_core запоминает его у себя при создании рантайма.
      let _entered = tokio.enter();
      JsRuntime::new(RuntimeOptions {
        extensions: vec![crate::canvas::kharos_canvas::init(), crate::gl::kharos_gl::init(), crate::web::kharos_web::init()],
        // Модули игра берёт с диска: собранную игру кладёт туда сборщик (см. `src/modules.rs`).
        module_loader: Some(Rc::new(crate::modules::FromDisk)),
        ..Default::default()
      })
    };
    Ok(Self { runtime, _tokio: tokio })
  }

  /// Запускает игру по адресу её сборки. Перед ней кладутся номера вызовов GL — их задаёт Rust, поэтому обёртка и
  /// привязки не могут разъехаться, — и сама обёртка браузерного API. Обе идут обычными скриптами, а не кодом
  /// расширения: от кода расширений deno_core требует семибитного ASCII, а в игре русские строки и комментарии.
  /// Игра же идёт модулем: её собирает Vite, как и для страницы, и `import.meta` в ней — обычное дело.
  pub fn run(&mut self, game: &ModuleSpecifier) -> Result<()> {
    self.runtime.execute_script("opcodes", crate::gl::opcodes_script())?;
    self.runtime.execute_script("prelude", PRELUDE.to_string())?;
    // Начало игры — дело не мгновенное: она сама берёт графику и заводит мир, а первый шаг после `await` ждёт,
    // пока прокрутится очередь задач V8. Её и прокручивает событийный цикл — как это делал бы браузер после
    // загрузки страницы. Работы в цикле, кроме самой игры, нет: кадры окно зовёт само (см. `frame`).
    let runtime = &mut self.runtime;
    self._tokio.block_on(async move {
      let module = runtime.load_main_es_module(game).await?;
      let evaluated = runtime.mod_evaluate(module);
      runtime.run_event_loop(Default::default()).await?;
      evaluated.await?;
      Ok(())
    })
  }

  /// Кадр окна: игра получает время игры в миллисекундах — как `requestAnimationFrame` в браузере.
  pub fn frame(&mut self, milliseconds: f64) -> Result<()> {
    self.runtime.execute_script("frame", format!("globalThis.__kharos_frame({milliseconds})"))?;
    Ok(())
  }

  /// Размер окна изменился: об этом игре говорят так же, как браузер говорил бы холсту. Плотность пикселей (ratio)
  /// идёт вместе с размером: по ней игра решает, сколько физических пикселей рисовать.
  pub fn resize(&mut self, width: f64, height: f64, ratio: f64) -> Result<()> {
    self.runtime.execute_script("resize", format!("globalThis.__kharos_resize({width}, {height}, {ratio})"))?;
    Ok(())
  }
}
