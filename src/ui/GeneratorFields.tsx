import { t, type Key } from '../i18n'
import { PEAK_RADIUS_LIMIT, type GeneratorConfig } from '../map/terrain'

interface Field<T> {
  key: keyof T
  /** Ключ подписи в словаре. */
  label: Key
  min: number
  max: number
  step: number
}

interface Group<T> {
  /** Ключ заголовка в словаре. */
  title: Key
  fields: Field<T>[]
}

const biomeFields = (prefix: 'salt' | 'red' | 'marsh'): Field<GeneratorConfig>[] => [
  { key: `${prefix}SwampShift`, label: 'generator.fields.swampShift', min: -0.2, max: 0.2, step: 0.005 },
  { key: `${prefix}RockShift`, label: 'generator.fields.rockShift', min: -0.2, max: 0.2, step: 0.005 },
  { key: `${prefix}PeakFactor`, label: 'generator.fields.peakFactor', min: 0, max: 4, step: 0.1 },
]

export const GENERATOR_GROUPS: Group<GeneratorConfig>[] = [
  {
    title: 'generator.groups.zones',
    fields: [
      { key: 'biomeScale', label: 'generator.fields.biomeScale', min: 40, max: 600, step: 10 },
      { key: 'zoneScale', label: 'generator.fields.zoneScale', min: 16, max: 160, step: 1 },
      { key: 'zoneWarp', label: 'generator.fields.zoneWarp', min: 0, max: 80, step: 1 },
      { key: 'swampLevel', label: 'generator.fields.swampLevel', min: 0, max: 1, step: 0.005 },
      { key: 'rockLevel', label: 'generator.fields.rockLevel', min: 0, max: 1, step: 0.005 },
    ],
  },
  {
    title: 'generator.groups.peaks',
    fields: [
      { key: 'peakChance', label: 'generator.fields.chance', min: 0, max: 1, step: 0.01 },
      { key: 'peakMinRadius', label: 'generator.fields.peakMinRadius', min: 0.5, max: PEAK_RADIUS_LIMIT, step: 0.05 },
      { key: 'peakMaxRadius', label: 'generator.fields.peakMaxRadius', min: 0.5, max: PEAK_RADIUS_LIMIT, step: 0.05 },
      { key: 'doubleChance', label: 'generator.fields.doubleChance', min: 0, max: 1, step: 0.01 },
      { key: 'tripleChance', label: 'generator.fields.tripleChance', min: 0, max: 1, step: 0.01 },
    ],
  },
  {
    title: 'generator.groups.deposits',
    fields: [
      { key: 'depositChance', label: 'generator.fields.chance', min: 0, max: 1, step: 0.01 },
      { key: 'depositRichness', label: 'generator.fields.depositRichness', min: 0.1, max: 5, step: 0.1 },
    ],
  },
  { title: 'generator.groups.salt', fields: biomeFields('salt') },
  { title: 'generator.groups.red', fields: biomeFields('red') },
  { title: 'generator.groups.marsh', fields: biomeFields('marsh') },
]

interface GroupsProps<T> {
  groups: Group<T>[]
  values: T
  onChange: (values: T) => void
}

/** Ползунки по группам: каждый меняет одно числовое поле values. */
export function Groups<T extends object>({ groups, values, onChange }: GroupsProps<T>) {
  return groups.map((group) => (
    <fieldset key={group.title} class="panel__group">
      <legend>{t(group.title)}</legend>
      {group.fields.map((field) => (
        <label key={String(field.key)} class="panel__row">
          <span>{t(field.label)}</span>
          <input
            type="range"
            min={field.min}
            max={field.max}
            step={field.step}
            value={values[field.key] as number}
            onInput={(event) => onChange({ ...values, [field.key]: Number(event.currentTarget.value) })}
          />
          <output>{values[field.key] as number}</output>
        </label>
      ))}
    </fieldset>
  ))
}
