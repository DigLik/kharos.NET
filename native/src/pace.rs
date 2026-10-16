//! Часы кадров, привязанные к смене экрана, — как время кадра у браузера (`requestAnimationFrame`).
//!
//! Смена буфера на macOS отпускает кадры неровно: на экране 120 Гц они идут парами — один сразу, следующий через
//! двойной период. Время, снятое настенными часами в начале кадра, дрожит так же (0,5 мс, потом 16 мс), а на экран
//! кадры выходят ровно, — и камера в движении дёргается. Здесь время кадра шагает целыми периодами экрана: на сколько
//! их ушло настоящее время, на столько и шаг, но не меньше одного. Так ровные кадры получают ровный шаг, пропущенный —
//! двойной, а часы не уходят от настоящих дальше полупериода.

pub struct Pace {
  /// Период экрана в миллисекундах.
  period: f64,
  /// Время прошлого кадра на часах кадров; None — кадров ещё не было.
  clock: Option<f64>,
}

impl Pace {
  /// hertz — частота экрана; без неё — 60 Гц.
  pub fn new(hertz: f64) -> Self {
    Self { period: 1000.0 / if hertz > 0.0 { hertz } else { 60.0 }, clock: None }
  }

  /// Время кадра по настоящему времени now (миллисекунды тех же часов, что у `performance.now()`). Отдаётся на период
  /// позже часов кадров: так оно не обгоняет `performance.now()` внутри кадра, как и у браузера.
  pub fn next(&mut self, now: f64) -> f64 {
    let clock = match self.clock {
      Some(last) => last + ((now - last) / self.period).round().max(1.0) * self.period,
      None => now,
    };
    self.clock = Some(clock);
    clock - self.period
  }
}

#[cfg(test)]
mod tests {
  use super::Pace;

  /// Экран 120 Гц: тот, на котором смену буфера и отпускает парами.
  const HERTZ: f64 = 120.0;
  const PERIOD: f64 = 1000.0 / HERTZ;

  /// Шаг часов сверяется с допуском: время приходит с дрожанием, и точное равенство с периодом не сойдётся —
  /// расхождение будет в последнем разряде.
  fn assert_step(previous: f64, next: f64, periods: f64) {
    let step = next - previous;
    assert!((step - periods * PERIOD).abs() < 1e-9, "шаг {step} мс вместо {periods} периодов по {PERIOD} мс");
  }

  /// Смена буфера отпускает кадры парами: один сразу (полпериода), другой через двойной период. До игры это дрожание
  /// не доходит — каждый кадр получает ровный шаг в один период.
  #[test]
  fn paired_frames_step_evenly() {
    let mut pace = Pace::new(HERTZ);
    let mut clock = pace.next(PERIOD);
    let mut now = PERIOD;
    for _ in 0..10 {
      for share in [0.5, 1.5] {
        now += PERIOD * share;
        let next = pace.next(now);
        assert_step(clock, next, 1.0);
        clock = next;
      }
    }
  }

  /// Пропущенный кадр игра видит двойным: часы навёрстывают его целиком, а не делят надвое.
  #[test]
  fn skipped_frame_steps_twice() {
    let mut pace = Pace::new(HERTZ);
    let mut clock = pace.next(PERIOD);
    // Кадр задержался на два периода, а часы окна при этом ещё и ушли вперёд на десятую долю периода.
    let next = pace.next(PERIOD + PERIOD * 2.1);
    assert_step(clock, next, 2.0);
    clock = next;
    let next = pace.next(PERIOD + PERIOD * 4.0);
    assert_step(clock, next, 2.0);
  }

  /// Кадр, пришедший раньше срока, не дробит шаг и не идёт назад: меньшего, чем период, не бывает.
  #[test]
  fn early_frame_still_steps_one_period() {
    let mut pace = Pace::new(HERTZ);
    let mut clock = pace.next(PERIOD);
    for now in [PERIOD * 1.4, PERIOD * 1.9, PERIOD * 2.0] {
      let next = pace.next(now);
      assert_step(clock, next, 1.0);
      clock = next;
    }
  }

  /// Частота экрана неизвестна — 60 Гц, как у обычного экрана; первый кадр идёт от настоящего времени.
  #[test]
  fn unknown_hertz_falls_back_to_60() {
    let period = 1000.0 / 60.0;
    let mut pace = Pace::new(0.0);
    let first = pace.next(100.0);
    assert_eq!(first, 100.0 - period);
    let next = pace.next(100.0 + period * 1.2);
    assert!((next - first - period).abs() < 1e-9);
  }
}
