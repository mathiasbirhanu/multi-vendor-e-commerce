// Preview session — cookie-based. The production auth is the .NET backend
// (JWT access + refresh, BCrypt hashes). This mock is for the sandbox preview only.
import { cookies } from 'next/headers'
import { NextResponse } from 'next/server'
import { db } from '@/lib/db'

export const SESSION_COOKIE = 'mh_uid'

export async function currentUser() {
  const store = await cookies()
  const uid = store.get(SESSION_COOKIE)?.value
  if (!uid) return null
  const user = await db.user.findUnique({
    where: { id: uid },
    include: { vendorProfile: true },
  })
  return user
}

export async function requireUser() {
  const user = await currentUser()
  if (!user) throw new HttpError(401, 'Authentication required')
  return user
}

export async function requireRole(role: 'Admin' | 'Vendor' | 'Buyer') {
  const user = await requireUser()
  if (user.role !== role) throw new HttpError(403, `${role} role required`)
  return user
}

export async function requireVendorApproved() {
  const user = await requireRole('Vendor')
  if (user.vendorProfile?.status !== 'Approved') throw new HttpError(403, 'Vendor account must be approved before listing products')
  return user
}

export function withSession(res: NextResponse, userId: string) {
  res.cookies.set(SESSION_COOKIE, userId, { httpOnly: true, sameSite: 'lax', path: '/', maxAge: 60 * 60 * 24 * 30 })
  return res
}

export class HttpError extends Error {
  constructor(public status: number, message: string) { super(message) }
}
