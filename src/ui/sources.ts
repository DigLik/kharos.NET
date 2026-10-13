import { t } from '../i18n'
import { BUILDING_TYPES, ORE_OF, PRODUCT_SPECS, RESOURCE_SPECS, buildingSpec, entriesOf, isOre, isProduct, type BuildingType, type Good, type Ore, type Product, type Resource, type ResourceSpec } from '../sim'

/** Какое здание перерабатывает руду и какое собирает изделие: первое такое по порядку видов. */
const REFINERIES = new Map<Ore, BuildingType>()
const PLANTS = new Map<Product, BuildingType>()
for (const type of BUILDING_TYPES) {
  const { refines, assembles } = buildingSpec(type)
  if (refines && !REFINERIES.has(refines)) REFINERIES.set(refines, type)
  if (assembles && !PLANTS.has(assembles)) PLANTS.set(assembles, type)
}

const named = (type: BuildingType | undefined) => (type ? t(`buildings.${type}`) : '?')

/** Что выйдет из месторождения и через что: «Металлическая руда → Плавильня → Металл». */
export function chainOf(resource: Resource) {
  const ore = ORE_OF[resource]
  return `${t(`goods.${ore}`)} → ${named(REFINERIES.get(ore))} → ${t(`goods.${resource}`)}`
}

/**
 * Откуда берётся груз — тому, кому его не хватает: ресурс — «Металлическая руда → Плавильня», и, если его продают
 * с орбиты, «или купить: Космопорт»; изделие — «Металл ×2, Кремний ×1 → Завод стройблоков». Руду даёт шахта: о ней
 * подсказки нет. Выводится из описаний зданий и рецептов, а не пишется текстом.
 */
export function sourceOf(good: Good) {
  if (isOre(good)) return ''
  if (isProduct(good)) {
    const recipe = entriesOf(PRODUCT_SPECS[good].recipe).map(([resource, amount]) => `${t(`goods.${resource}`)} ×${amount}`)
    return `${recipe.join(', ')} → ${named(PLANTS.get(good))}`
  }
  const ore = ORE_OF[good]
  const made = `${t(`goods.${ore}`)} → ${named(REFINERIES.get(ore))}`
  // Только своя добыча или закупка после исследования: покупку не обещаем.
  const spec: ResourceSpec = RESOURCE_SPECS[good]
  return spec.mined || spec.research ? made : `${made} ${t('hud.orBuy', { building: t('buildings.spaceport') })}`
}
