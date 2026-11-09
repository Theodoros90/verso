import { Link } from 'react-router'

export function NotFoundPage() {
  return (
    <div className="px-4 pt-6 pb-10 md:px-7">
      <p className="text-ink-2">
        This page doesn’t exist.{' '}
        <Link to="/" className="text-cyan underline">
          Go to Overview
        </Link>
      </p>
    </div>
  )
}
