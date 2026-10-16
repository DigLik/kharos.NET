//! Замер кадров (`KHAROS_PROFILE=1`): раз в секунду в журнал — сколько кадров, сколько миллисекунд ушло на кадр игры
//! (JS, вызовы GL и холста) и на показ (смена буфера, в ней же ожидание смены экрана), в среднем и в худшем, и
//! сколько кадров не уложились в смену экрана, а промежутки между кадрами — по долям смены экрана (короче 0,7, ровные,
//! двойные, дольше): по ним виден ритм показа. Шаги часов кадров (один период, два, больше) — то, что видит игра. Средний FPS проседаний не видит: их видно по худшему кадру.

use std::collections::HashMap;
use std::time::{Duration, Instant};

pub struct Profile {
  /// Длительность смены экрана: кадр дольше полутора таких — пропуск.
  period: Duration,
  since: Instant,
  last: Option<Instant>,
  frames: u32,
  game: Duration,
  game_max: Duration,
  show: Duration,
  show_max: Duration,
  gap_max: Duration,
  missed: u32,
  /// Промежутки между кадрами в долях смены экрана: меньше 0,7, до 1,3, до 2,5 и дольше.
  gaps: [u32; 4],
  /// Шаги часов кадров (см. `pace.rs`) в периодах экрана: один, два, больше.
  steps: [u32; 3],
  clock: Option<f64>,
  /// Рисование двумерных холстов на процессоре: номер — размер, сумма за секунду и худший кадр.
  sheets: HashMap<u32, (u32, u32, Duration, Duration)>,
}

impl Profile {
  /// Замер, если его просили (`KHAROS_PROFILE`); hertz — частота экрана.
  pub fn from_env(hertz: f64) -> Option<Self> {
    std::env::var_os("KHAROS_PROFILE")?;
    crate::canvas::measure_costs();
    let hertz = if hertz > 0.0 { hertz } else { 60.0 };
    println!("замер кадров: экран {hertz:.0} Гц");
    Some(Self { period: Duration::from_secs_f64(1.0 / hertz), since: Instant::now(), last: None, frames: 0, game: Duration::ZERO, game_max: Duration::ZERO, show: Duration::ZERO, show_max: Duration::ZERO, gap_max: Duration::ZERO, missed: 0, gaps: [0; 4], steps: [0; 3], clock: None, sheets: HashMap::new() })
  }

  /// Кадр: начался в started, игра закончила в drawn, показ — в shown; clock — время кадра, отданное игре (мс).
  pub fn frame(&mut self, started: Instant, drawn: Instant, shown: Instant, clock: f64) {
    if let Some(last) = self.clock {
      let periods = ((clock - last) / (self.period.as_secs_f64() * 1000.0)).round() as usize;
      self.steps[periods.clamp(1, 3) - 1] += 1;
    }
    self.clock = Some(clock);
    for (id, width, height, time) in crate::canvas::take_costs() {
      let sheet = self.sheets.entry(id).or_insert((width, height, Duration::ZERO, Duration::ZERO));
      *sheet = (width, height, sheet.2 + time, sheet.3.max(time));
    }
    let (game, show) = (drawn - started, shown - drawn);
    if let Some(last) = self.last {
      let gap = started - last;
      self.gap_max = self.gap_max.max(gap);
      if gap > self.period * 3 / 2 {
        self.missed += 1;
      }
      let share = gap.as_secs_f64() / self.period.as_secs_f64();
      self.gaps[[0.7, 1.3, 2.5].iter().position(|limit| share < *limit).unwrap_or(3)] += 1;
    }
    self.last = Some(started);
    self.frames += 1;
    self.game += game;
    self.game_max = self.game_max.max(game);
    self.show += show;
    self.show_max = self.show_max.max(show);
    if shown - self.since < Duration::from_secs(1) {
      return;
    }
    let ms = |duration: Duration| duration.as_secs_f64() * 1000.0;
    let frames = self.frames.max(1);
    println!("кадры: {} за {:.0} мс, игра {:.1}/{:.1} мс, показ {:.1}/{:.1} мс, худший промежуток {:.1} мс, пропусков {}, промежутки {:?}, шаги часов {:?}", self.frames, ms(shown - self.since), ms(self.game) / frames as f64, ms(self.game_max), ms(self.show) / frames as f64, ms(self.show_max), ms(self.gap_max), self.missed, self.gaps, self.steps,);
    // Дороже всего рисуемые холсты: размер, в среднем на кадр и худший кадр.
    let mut sheets: Vec<_> = self.sheets.drain().collect();
    sheets.sort_by(|a, b| b.1 .2.cmp(&a.1 .2));
    let sheets: Vec<String> = sheets.iter().take(4).map(|(id, (width, height, sum, max))| format!("#{id} {width}×{height} {:.1}/{:.1} мс", ms(*sum) / frames as f64, ms(*max))).collect();
    if !sheets.is_empty() {
      println!("  холсты: {}", sheets.join(", "));
    }
    // Новая секунда: сумма и худшее — заново, а прошлый кадр остаётся — от него считается промежуток.
    self.since = shown;
    self.frames = 0;
    self.missed = 0;
    self.gaps = [0; 4];
    self.steps = [0; 3];
    (self.game, self.game_max, self.show, self.show_max, self.gap_max) = Default::default();
  }
}
