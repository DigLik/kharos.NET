import { t } from '@kharos/i18n';
import { WEAPONS, turretSpec, unitSight, unitSpec, type UnitType } from '@kharos/sim-ts';

type WeaponType = keyof typeof WEAPONS;
type Target = 'infantry' | 'vehicle' | 'heavy' | 'air' | 'building';

/** Оружие как его читает карточка: необязательные поля есть не у каждого вида. */
interface Weapon {
  range: number;
  damage: number;
  reload: number;
  splash?: number;
  chain?: number;
  minRange?: number;
  lob?: boolean;
  air: boolean;
  onlyAir?: boolean;
  vs?: Partial<Record<Target, number>>;
}

/** Строка параметров юнита: что это (key — для тех, кому нужны не все строки), подпись и значение. */
export interface StatRow {
  key: 'hp' | 'class' | 'speed' | 'sight' | 'cargo' | 'repair' | 'damage' | 'reload' | 'dps' | 'range' | 'splash' | 'chain' | 'targets' | 'strong' | 'weak';
  label: string;
  value: string;
}

/** До одного знака после запятой, без хвостового нуля. */
const tidy = (value: number) => String(Math.round(value * 10) / 10);

/**
 * Параметры вида юнита для карточки и панели: прочность, класс, скорость, обзор и по блоку строк на каждое оружие —
 * своё и на турелях, одинаковые турели — одним блоком со счётчиком. Собирается из UNITS, TURRETS и WEAPONS, а не
 * пишется текстом: поменяются числа — поменяется и карточка.
 */
export function unitStats(type: UnitType): StatRow[] {
  const spec = unitSpec(type);
  const rows: StatRow[] = [];
  const row = (key: StatRow['key'], value: string | number) => rows.push({ key, label: t(`stats.${key}`), value: String(value) });
  row('hp', spec.hp);
  row('class', t(`stats.vs.${spec.kind}`));
  row('speed', tidy(spec.speed));
  row('sight', tidy(unitSight(type)));
  if (spec.inventory) row('cargo', spec.inventory);

  const weapons = new Map<WeaponType, number>();
  const arm = (weapon: WeaponType | undefined) => weapon && weapons.set(weapon, (weapons.get(weapon) ?? 0) + 1);
  arm(spec.weapon);
  let repair = spec.repair;
  for (const { turret } of spec.mounts ?? []) {
    const mounted = turretSpec(turret);
    arm(mounted.weapon);
    repair ??= mounted.repair;
  }
  if (repair) row('repair', tidy(repair));

  for (const [name, count] of weapons) {
    const weapon: Weapon = WEAPONS[name];
    row('damage', count > 1 ? `${weapon.damage} ×${count}` : weapon.damage);
    row('reload', t('stats.seconds', { n: tidy(weapon.reload) }));
    row('dps', tidy((weapon.damage * count) / weapon.reload));
    row('range', weapon.minRange ? `${tidy(weapon.minRange)}–${tidy(weapon.range)}` : tidy(weapon.range));
    if (weapon.splash) row('splash', tidy(weapon.splash));
    if (weapon.chain) row('chain', weapon.chain);
    const targets = t(weapon.onlyAir ? 'stats.air' : weapon.air ? 'stats.both' : 'stats.ground');
    row('targets', weapon.lob ? `${targets}, ${t('stats.overWalls')}` : targets);
    // По кому оружие бьёт заметно иначе: сначала самое сильное отличие.
    const factors = Object.entries(weapon.vs ?? {}) as [Target, number][];
    const list = (items: [Target, number][]) => items.map(([target, factor]) => `${t(`stats.vs.${target}`)} ×${factor}`).join(', ');
    const strong = factors.filter(([, factor]) => factor > 1).sort((a, b) => b[1] - a[1]);
    const weak = factors.filter(([, factor]) => factor < 1).sort((a, b) => a[1] - b[1]);
    if (strong.length) row('strong', list(strong));
    if (weak.length) row('weak', list(weak));
  }
  return rows;
}

/** Параметры одним текстом, строка на параметр: для всплывающей подсказки. */
export const unitStatsText = (type: UnitType) => unitStats(type).map(({ label, value }) => `${label}: ${value}`).join('\n');

/** Главное одной строкой — для панели выбранного: прочность, скорость, обзор, урон и дальность каждого оружия. */
export function unitStatsLine(type: UnitType) {
  const brief: StatRow['key'][] = ['hp', 'speed', 'sight', 'cargo', 'damage', 'range'];
  return unitStats(type).filter(({ key }) => brief.includes(key)).map(({ label, value }) => `${label} ${value}`).join(' · ');
}
