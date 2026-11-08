// Outbound messaging — server-side only.
// Mirrors the .NET notification stack: ISmsSender / IEmailSender adapters (Mock | EthioTelecom | Safaricom | Smtp).
// In this preview every "send" writes an OutboundMessage row (provider = SmsProvider/EmailProvider setting),
// so the admin Ops tab can inspect exactly what the real adapters would have delivered.
import { db } from '@/lib/db'
import { getSetting } from '../server/handlers/store'

export type OutboundChannel = 'Sms' | 'Email'

/** Templates that always fan out to SMS (+ email unless SMS-only). Mirrors the .NET NotificationDispatcher. */
export const CRITICAL_TEMPLATES = new Set([
  'OrderConfirmed', 'OrderShipped', 'OrderDelivered', 'PayoutProcessed',
  'ReturnDecided', 'VendorApproved', 'AbandonedCart',
])
const SMS_ONLY = new Set(['AbandonedCart'])

export type DispatchPayload = { subject?: string; body?: string; [key: string]: unknown }
export type DispatchUser = { id: string; phone?: string | null; email?: string | null; fullName?: string | null }

async function send(channel: OutboundChannel, to: string | null | undefined, template: string, payload: Record<string, unknown>) {
  const provider = await getSetting(channel === 'Sms' ? 'SmsProvider' : 'EmailProvider', 'Mock')
  if (!to || !String(to).trim()) {
    // No destination on file — record the skip so ops can see it, do not fail the caller.
    return db.outboundMessage.create({
      data: { channel, template, destination: '', provider, payloadJson: JSON.stringify(payload), status: 'Skipped', error: 'No destination on file' },
    })
  }
  return db.outboundMessage.create({
    data: {
      channel, template, destination: String(to), provider,
      payloadJson: JSON.stringify(payload), providerRef: `MOCK-${channel.toUpperCase()}-${Date.now()}`,
      status: 'Sent', sentAt: new Date(),
    },
  })
}

/** Send an SMS via the configured provider adapter (writes an OutboundMessage row). */
export const sendSms = (to: string | null | undefined, template: string, payload: Record<string, unknown> = {}) =>
  send('Sms', to, template, payload)

/** Send an email via the configured provider adapter (writes an OutboundMessage row). */
export const sendEmail = (to: string | null | undefined, template: string, payload: Record<string, unknown> = {}) =>
  send('Email', to, template, payload)

/**
 * Full notify: InApp notification for everyone + SMS (and email, unless SMS-only) for critical templates.
 * Drop-in upgrade over `notify` — safe for any template name.
 */
export async function dispatch(template: string, user: DispatchUser | null | undefined, payload: DispatchPayload = {}) {
  if (!user) return
  const subject = payload.subject ?? template
  const body = payload.body ?? ''
  await db.notification.create({ data: { userId: user.id, channel: 'InApp', type: template, subject, body } })
  if (!CRITICAL_TEMPLATES.has(template)) return
  await sendSms(user.phone, template, { ...payload, recipient: user.fullName ?? null })
  if (!SMS_ONLY.has(template)) await sendEmail(user.email, template, { ...payload, recipient: user.fullName ?? null })
}
