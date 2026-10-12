import { useMemo, useState } from 'preact/hooks'
import type { TechState, TopicView } from '../game/hud'
import { goodIcon } from '../game/goodIcons'
import { buildingPortrait, levelPortrait, pavePortrait, unitPortrait } from '../game/portraits'
import type { Command, LabLack, Topic } from '../sim'
import { t } from '../i18n'
import { Res, Stacks } from './Hud'

/** Стороны значков в пикселях экрана: портреты рисуются ровно под них. */
const ICON = 48
const SMALL = 24

interface TechTreeProps {
  tech: TechState
  send: (command: Command) => void
  close: () => void
}

/** Руда, которую находит открытие месторождения: её значок и рисуется на теме. */
const FOUND = { kharite: 'khariteOre', lithium: 'lithiumOre', uranium: 'uraniumOre' } as const

/** Значок темы: модернизация — здание со стрелками уровня, находка — руда, остальное — первое, что тема открывает. */
function iconOf(view: TopicView, size: number) {
  if (view.topic === 'levels2' || view.topic === 'levels3') return levelPortrait(view.topic === 'levels2' ? 2 : 3, size)
  if (view.trigger && view.trigger in FOUND) return goodIcon(FOUND[view.trigger as keyof typeof FOUND])
  if (view.paves.length) return pavePortrait(view.paves[0], size)
  if (view.buildings.length) return buildingPortrait(view.buildings[0], size)
  if (view.units.length) return unitPortrait(view.units[0], size)
  return levelPortrait(1, size)
}

/** Почему лаборатории стоят: «кредитов», «энергии» или название груза. */
const lackName = (lack: LabLack) => (lack === 'credits' ? t('tech.lackCredits') : lack === 'power' || lack === 'idle' ? t('tech.lackPower') : t(`goods.${lack}`))

function Tile({ view, send, hover }: { view: TopicView; send: TechTreeProps['send']; hover: (view: TopicView | null, element?: HTMLElement) => void }) {
  // Тему-открытие и открытую в очередь не ставят.
  const idle = view.status === 'done' || !!view.trigger
  return (
    <button
      class={`tech__tile is-${view.status}${view.order ? ' is-queued' : ''}`}
      aria-disabled={idle}
      onClick={(event) => !idle && send({ type: 'research', topic: view.topic, front: event.shiftKey })}
      onMouseEnter={(event) => hover(view, event.currentTarget)}
      onMouseLeave={() => hover(null)}
    >
      <span class="tech__icon">
        <img src={iconOf(view, ICON)} alt="" />
        {view.order > 0 && <b class="tech__order">{view.order}</b>}
        {view.status !== 'done' && view.progress > 0 && (
          <span class="tech__bar">
            <span style={{ width: `${Math.round(view.progress * 100)}%` }} />
          </span>
        )}
      </span>
      <span class="tech__name">{t(`research.${view.topic}`)}</span>
      {view.status !== 'done' && !view.trigger && (
        <span class="tech__price">
          <span class="tech__credits">
            <span class="hud__coin" />
            {view.cost}
          </span>
          <span class="tech__time">{view.time}</span>
        </span>
      )}
      {view.trigger && view.status !== 'done' && <span class="tech__trigger">{t(`tech.triggers.${view.trigger}`)}</span>}
    </button>
  )
}

/** Строка темы в подсказке: значок, название цвета её доступности. */
const Line = ({ view }: { view: TopicView }) => (
  <span class={`tech__line is-${view.status}`}>
    <img src={iconOf(view, SMALL)} alt="" />
    {t(`research.${view.topic}`)}
  </span>
)

/** Дерево того, что зависит от темы: каждая тема — один раз, там, где встретилась впервые. */
function Dependents({ topic, byTopic, children, seen }: { topic: Topic; byTopic: Map<Topic, TopicView>; children: Map<Topic, Topic[]>; seen: Set<Topic> }) {
  const next = (children.get(topic) ?? []).filter((child) => !seen.has(child))
  next.forEach((child) => seen.add(child))
  if (!next.length) return null
  return (
    <ul>
      {next.map((child) => (
        <li key={child}>
          <Line view={byTopic.get(child)!} />
          <Dependents topic={child} byTopic={byTopic} children={children} seen={seen} />
        </li>
      ))}
    </ul>
  )
}

/** Подсказка темы: что она даёт и открывает, цена или условие, что нужно раньше и что откроется дальше — деревом. */
function Card({ view, byTopic, children, at }: { view: TopicView; byTopic: Map<Topic, TopicView>; children: Map<Topic, Topic[]>; at: DOMRect }) {
  // Справа от плитки, а у правого края экрана — слева от неё.
  const left = at.right + 8 + 340 > window.innerWidth ? undefined : at.right + 8
  const right = left === undefined ? window.innerWidth - at.left + 8 : undefined
  // Сверху вровень с плиткой, а в нижней половине экрана — снизу вровень: дерево зависимых бывает длинным.
  const low = at.top > window.innerHeight / 2
  const top = low ? undefined : at.top
  const bottom = low ? Math.max(8, window.innerHeight - at.bottom) : undefined
  const opens = [
    ...view.buildings.map((type) => ({ key: type, src: buildingPortrait(type, SMALL), name: t(`buildings.${type}`) })),
    ...view.units.map((type) => ({ key: type, src: unitPortrait(type, SMALL), name: t(`units.${type}`) })),
    ...view.paves.map((kind) => ({ key: kind, src: pavePortrait(kind, SMALL), name: t(`grid.tools.${kind}`) })),
  ]
  return (
    <div class="hud tech__card" style={{ left: left === undefined ? undefined : `${left}px`, right: right === undefined ? undefined : `${right}px`, top: top === undefined ? undefined : `${top}px`, bottom: bottom === undefined ? undefined : `${bottom}px` }}>
      <strong>{t(`research.${view.topic}`)}</strong>
      <span class={`tech__state is-${view.status}`}>{view.trigger && view.status !== 'done' ? t('tech.triggerTopic') : t(`tech.status.${view.status}`)}</span>
      <span>{t(`researchInfo.${view.topic}`)}</span>
      {opens.length > 0 && (
        <span class="tech__opens">
          {opens.map(({ key, src, name }) => (
            <span key={key}>
              <img src={src} alt="" />
              {name}
            </span>
          ))}
        </span>
      )}
      {view.trigger
        ? view.status !== 'done' && <span class="tech__trigger">{t(`tech.triggers.${view.trigger}`)}</span>
        : view.status !== 'done' && (
            <span class="tech__price">
              <span class="tech__credits">
                <span class="hud__coin" />
                {view.cost}
              </span>
              <Stacks items={view.materials} />
              <span class="tech__time">{t('tech.time', { time: view.time })}</span>
            </span>
          )}
      {view.after.length > 0 && (
        <div class="tech__deps">
          <small>{t('tech.before')}</small>
          {view.after.map((before) => <Line key={before} view={byTopic.get(before)!} />)}
        </div>
      )}
      {(children.get(view.topic)?.length ?? 0) > 0 && (
        <div class="tech__deps">
          <small>{t('tech.leadsTo')}</small>
          <Dependents topic={view.topic} byTopic={byTopic} children={children} seen={new Set([view.topic])} />
        </div>
      )}
    </div>
  )
}

/**
 * Окно исследований, как в Factorio: сверху очередь, ниже все темы плитками по порядку зависимостей — сначала то, что
 * нужно раньше. Зелёные можно исследовать сейчас, жёлтые станут можно, когда очередь дойдёт до нужного им, красные —
 * пока нельзя. Щелчок ставит тему в конец очереди вместе с недостающими предшественниками, Shift — в начало; щелчок
 * по теме в очереди убирает её. Наведение показывает, что тема даёт и что от неё зависит — деревом.
 */
export function TechTree({ tech, send, close }: TechTreeProps) {
  const [hovered, setHovered] = useState<{ topic: Topic; at: DOMRect } | null>(null)
  const byTopic = useMemo(() => new Map(tech.topics.map((view) => [view.topic, view])), [tech.topics])
  const children = useMemo(() => {
    const map = new Map<Topic, Topic[]>()
    for (const view of tech.topics) for (const before of view.after) map.set(before, [...(map.get(before) ?? []), view.topic])
    return map
  }, [tech.topics])
  // По ярусам, в ярусе — как в описании; открытое — отдельно, в конце.
  const order = (list: TopicView[]) => [...list].sort((a, b) => a.tier - b.tier)
  const open = order(tech.topics.filter((view) => view.status !== 'done'))
  const done = order(tech.topics.filter((view) => view.status === 'done'))
  const hover = (view: TopicView | null, element?: HTMLElement) => setHovered(view && element ? { topic: view.topic, at: element.getBoundingClientRect() } : null)

  const { labs, plan, left } = tech
  const current = plan[0]
  const shown = hovered && byTopic.get(hovered.topic)

  // Подсказка — рядом с окном, а не внутри: окно сдвинуто через transform, и fixed внутри него считался бы от окна.
  return (
    <>
    <section class="hud tech" aria-label={t('tech.title')}>
      <header class="tech__head">
        <h2>{t('tech.title')}</h2>
        <span class="tech__labs">{labs.count === 0 ? t('tech.noLabs') : t('tech.labs', { count: labs.count, working: labs.working, speed: labs.speed })}</span>
        <button class="tech__close" data-tip={t('tech.closeTip')} onClick={close}>
          ✕
        </button>
      </header>

      <div class="tech__queue">
        <span class="tech__label">{t('tech.queue')}</span>
        {plan.length ? (
          plan.map((topic, i) => {
            const view = byTopic.get(topic)!
            return (
              <button key={topic} class={i === 0 ? 'tech__queued is-current' : 'tech__queued'} data-tip={t('tech.dequeueTip')} onClick={() => send({ type: 'dequeue', topic })}>
                <img src={iconOf(view, SMALL)} alt="" />
                {t(`research.${topic}`)}
                {i === 0 && (
                  <small>
                    {Math.round(view.progress * 100)}%{left ? ` · ${left}` : ''}
                  </small>
                )}
              </button>
            )
          })
        ) : (
          <span class="tech__empty">{t('tech.queueEmpty')}</span>
        )}
        {plan.length > 0 && (
          <button class="tech__clear" data-tip={t('tech.cancelTip')} onClick={() => send({ type: 'cancelResearch' })}>
            {t('tech.cancel')}
          </button>
        )}
      </div>
      {current && labs.lacks.length > 0 && (
        <div class="tech__lacks">
          {t('tech.lacks')}
          {labs.lacks.map(({ lack, count }) => (
            <span key={lack} class="tech__lack">
              {lack !== 'credits' && lack !== 'power' && lack !== 'idle' ? <Res resource={lack} /> : null}
              {lackName(lack)} <small>{t('tech.labCount', { count })}</small>
            </span>
          ))}
        </div>
      )}

      <div class="tech__list">
        <div class="tech__grid">
          {open.map((view) => <Tile key={view.topic} view={view} send={send} hover={hover} />)}
        </div>
        {done.length > 0 && (
          <>
            <h3>{t('tech.researched')}</h3>
            <div class="tech__grid">
              {done.map((view) => <Tile key={view.topic} view={view} send={send} hover={hover} />)}
            </div>
          </>
        )}
      </div>
      <footer class="tech__foot">{t('tech.hint')}</footer>
    </section>
    {shown && hovered && <Card view={shown} byTopic={byTopic} children={children} at={hovered.at} />}
    </>
  )
}
