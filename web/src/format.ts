export const formatDuration = (seconds: number) =>
  seconds > 0 ? `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}` : ''

export const formatPrice = (price: number, currency: string) =>
  new Intl.NumberFormat(undefined, { style: 'currency', currency: currency || 'EUR' }).format(price)
