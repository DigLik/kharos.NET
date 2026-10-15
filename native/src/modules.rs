//! Откуда рантайм берёт код игры. В браузере модули приносит сеть по адресу, а здесь их место занимает диск:
//! собранная игра лежит рядом с окном, и `import.meta.url` в ней указывает на тот же файл, что открыла оболочка.
//! Всё, что игра тянет за собой (шейдеры, картинки, словари), вшито в сборку — её собирает Vite так же, как для
//! страницы, — поэтому других адресов у окна пока нет.

use std::fs;

use deno_core::error::ModuleLoaderError;
use deno_core::{
  resolve_import, ModuleLoadOptions, ModuleLoadReferrer, ModuleLoadResponse, ModuleLoader, ModuleSource, ModuleSourceCode,
  ModuleSpecifier, ModuleType, ResolutionKind,
};
use deno_error::JsErrorBox;

/// Загрузчик модулей окна: адрес модуля — это путь на диске.
pub struct FromDisk;

impl ModuleLoader for FromDisk {
  /// Разрешает имя модуля в адрес: главному адрес выбирает оболочка (см. `main.rs`), а остальные игра пишет
  /// относительно себя — как у воркеров, которых в собранной игре несколько.
  fn resolve(&self, specifier: &str, referrer: &str, _kind: ResolutionKind) -> Result<ModuleSpecifier, ModuleLoaderError> {
    resolve_import(specifier, referrer).map_err(JsErrorBox::from_err)
  }

  /// Читает модуль. Всё идёт сразу: диск рядом, а ждать, как сеть, нечего — поэтому и ответ синхронный.
  fn load(
    &self,
    module_specifier: &ModuleSpecifier,
    _maybe_referrer: Option<&ModuleLoadReferrer>,
    _options: ModuleLoadOptions,
  ) -> ModuleLoadResponse {
    ModuleLoadResponse::Sync(read(module_specifier))
  }
}

/// Код модуля с диска. О том, что файла нет, рантайм узнаёт отсюда — этим и говорит, чего игре не хватило.
fn read(specifier: &ModuleSpecifier) -> Result<ModuleSource, ModuleLoaderError> {
  let path = specifier
    .to_file_path()
    .map_err(|_| JsErrorBox::generic(format!("модуль не с диска: {specifier}")))?;
  let code = fs::read_to_string(&path)
    .map_err(|error| JsErrorBox::generic(format!("модуль не прочитан ({}): {error}", path.display())))?;
  Ok(ModuleSource::new(ModuleType::JavaScript, ModuleSourceCode::String(code.into()), specifier, None))
}
