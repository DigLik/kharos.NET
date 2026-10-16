//! Арифметика с правилами JS: движок и системы на Rust обязаны считать так же, как TS, до последнего знака.

/// Полный оборот в радианах.
pub const TURN: f64 = std::f64::consts::PI * 2.0;

/// `Math.round` из JS: половина — вверх (к +∞), а не от нуля, как `f64::round`.
pub fn js_round(x: f64) -> f64 {
  let floor = x.floor();
  if x - floor >= 0.5 {
    floor + 1.0
  } else {
    floor
  }
}

/// `Math.sign` из JS: у нуля — ноль, а не единица, как у `f64::signum`.
pub fn js_sign(x: f64) -> f64 {
  if x > 0.0 {
    1.0
  } else if x < 0.0 {
    -1.0
  } else {
    x
  }
}

/// Угол, приведённый к промежутку от -π до π (`wrap` в common.ts).
pub fn wrap(angle: f64) -> f64 {
  angle - TURN * js_round(angle / TURN)
}

/// Куда будет смотреть тот, кто поворачивается от facing к wanted не больше чем на max_turn радиан (`turnToward`).
pub fn turn_toward(facing: f64, wanted: f64, max_turn: f64) -> f64 {
  let off = wrap(wanted - facing);
  if off.abs() <= max_turn {
    wanted
  } else {
    wrap(facing + js_sign(off) * max_turn)
  }
}

#[cfg(test)]
mod tests {
  use super::*;

  #[test]
  fn rounding_and_sign_follow_js() {
    assert_eq!(js_round(-2.5), -2.0);
    assert_eq!(js_round(2.5), 3.0);
    assert_eq!(js_round(0.49999999999999994), 0.0);
    assert_eq!(js_sign(0.0), 0.0);
    assert_eq!(js_sign(-3.0), -1.0);
  }

  #[test]
  fn turning_takes_the_short_way() {
    assert_eq!(turn_toward(0.0, 1.0, 2.0), 1.0);
    assert_eq!(turn_toward(0.0, 1.0, 0.25), 0.25);
    assert!((turn_toward(3.0, -3.0, 0.1) - 3.1).abs() < 1e-12);
  }
}
