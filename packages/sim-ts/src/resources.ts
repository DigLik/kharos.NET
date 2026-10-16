import resources from '@kharos/base/resources.json'
import ores from '@kharos/base/ores.json'
import products from '@kharos/base/products.json'

/**
 * Что симуляция знает о грузе. Как он называется и выглядит, знает клиент: см. ui/names.ts.
 * Груз лежит на складах (Inventory) и возится грузовиками; см. inventory.ts и logistics.ts.
 *
 * Груз бывает трёх родов. Руда — то, что даёт шахта: её не продают и в рецепты она не идёт, только в переработку.
 * Ресурс — то, что выходит с переработки: его продают, из него строят и делают юнитов. Изделие — то, что собирает
 * из ресурсов сборочный цех: его не продают, оно идёт на стройку, в турели и на верхний тир (см. assembly.ts).
 * Ресурсы и изделия вместе — готовое: оно лежит в хранилищах.
 */
export interface ResourceSpec {
  /** Цена единицы при продаже через космопорт, в кредитах. */
  price: number
  /** С орбиты не закупается: только своя добыча, чужая — набегом или подарком союзника. */
  mined?: boolean
  /**
   * Тема (Topic), без которой ресурс с орбиты не купить: сначала научись его делать. Строка, а не Topic: темы сами
   * ссылаются на грузы; имя сверяет тест.
   */
  research?: string
  /** Пояснение к записи для того, кто правит данные; игре не нужно. */
  about?: string
}

/**
 * Готовые ресурсы — данные мода base (packages/base/data/resources.json): их продают через космопорт, из них строят
 * здание и делают технику.
 */
export const RESOURCE_SPECS = resources as Record<keyof typeof resources, ResourceSpec>

export type Resource = keyof typeof RESOURCE_SPECS
export const RESOURCES = Object.keys(RESOURCE_SPECS) as Resource[]

/** Виды руды и то, что из них выходит на переработке: packages/base/data/ores.json. */
export const ORE_SPECS = ores as Record<keyof typeof ores, { resource: Resource; about?: string }>

export type Ore = keyof typeof ORE_SPECS
export const ORES = Object.keys(ORE_SPECS) as Ore[]

/** Что выходит из этого вида руды: переработка делает из руды металла металл. */
export const resourceOf = (ore: Ore): Resource => ORE_SPECS[ore].resource

/** Чем добывают этот ресурс: месторождение металла даёт металлическую руду. */
export const ORE_OF = Object.fromEntries(ORES.map((ore) => [ORE_SPECS[ore].resource, ore])) as Record<Resource, Ore>

/**
 * Рецепт изделия: что уходит на одну сборку и сколько штук она даёт. Нормы нет: цех собирает, пока готовому есть
 * куда лечь, — хранилища не бесконечны, а лишний цех игрок выключает сам.
 */
export interface ProductSpec {
  recipe: Partial<Record<Resource, number>>
  yield: number
  /** Пояснение к записи для того, кто правит данные; игре не нужно. */
  about?: string
}

/**
 * Изделия сборочного цеха — данные мода base (packages/base/data/products.json), по одному на каждую петлю игры (§3.7
 * design.md). Их не продают: космопорт торгует только ресурсами.
 */
export const PRODUCT_SPECS = products as Record<keyof typeof products, ProductSpec>

export type Product = keyof typeof PRODUCT_SPECS
export const PRODUCTS = Object.keys(PRODUCT_SPECS) as Product[]
export const productSpec = (product: Product): ProductSpec => PRODUCT_SPECS[product]

/** На сколько сборок цех держит сырьё про запас: больше он у зоны не просит. */
const STOCKED_CYCLES = 4

/** Сколько какого сырья цех держит под рецепт: столько он и заказывает у зоны. */
export const stockedFor = (product: Product): Amounts =>
  Object.fromEntries(Object.entries(productSpec(product).recipe).map(([resource, amount]) => [resource, amount * STOCKED_CYCLES]))

/** Готовое: ресурсы и изделия. Его хранят хранилища и тратят стройки и производство. */
export type Ware = Resource | Product
export const WARES: Ware[] = [...RESOURCES, ...PRODUCTS]

/** Всё, что лежит на складах, — и руда, и готовое. */
export type Good = Ware | Ore
export const GOODS: Good[] = [...WARES, ...ORES]

/** Руда это или готовое: руду в рецепты не берут, готовое в переработку не кладут. */
export const isOre = (good: Good): good is Ore => ORES.includes(good as Ore)

/** Изделие ли это: его собирает цех и не продаёт космопорт. */
export const isProduct = (good: Good): good is Product => PRODUCTS.includes(good as Product)

/** Сколько чего угодно: например, цена в материалах, рецепт или содержимое склада. */
export type Amounts = Partial<Record<Good, number>>

/** Что лежит в amounts, парами груз — количество, без нулей. */
export const entriesOf = (amounts: Amounts) =>
  (Object.entries(amounts) as [Good, number][]).filter(([, amount]) => amount > 0)

/** Сколько всего в amounts, всего груза вместе. */
export const totalOf = (amounts: Amounts) => entriesOf(amounts).reduce((sum, [, amount]) => sum + amount, 0)
