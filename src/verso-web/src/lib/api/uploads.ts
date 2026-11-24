/** Upload endpoints of a workflow with a File upload start (docs/api.md, ADR 0023). */
import { getAccessToken, UNAUTHORIZED_EVENT } from '../auth'
import { ApiError, request, type Problem } from './client'
import type { SubmissionDto, UploadSettingsDto } from './generated'

export interface UploadResult {
  submission: SubmissionDto
  /** True when the same file was uploaded before (HTTP 200): the submission is the first run, not a new one. */
  alreadyUploaded: boolean
}

const path = (key: string) => `/api/v1/workflows/${encodeURIComponent(key)}`

export const uploadsApi = {
  settings: (key: string, signal?: AbortSignal) => request<UploadSettingsDto>(`${path(key)}/upload-settings`, { signal }).then((r) => r.body),

  /**
   * Sends the file as multipart/form-data with the signed-in user's bearer token. XMLHttpRequest is used because
   * fetch can't report upload progress. A refused or interrupted connection becomes an ApiError with status 0.
   */
  async upload(key: string, file: File, onProgress?: (fraction: number) => void): Promise<UploadResult> {
    const token = await getAccessToken()
    const form = new FormData()
    form.append('file', file)
    return new Promise<UploadResult>((resolve, reject) => {
      const xhr = new XMLHttpRequest()
      xhr.open('POST', `${path(key)}/uploads`)
      xhr.setRequestHeader('Accept', 'application/json')
      if (token) xhr.setRequestHeader('Authorization', `Bearer ${token}`)
      xhr.upload.onprogress = (e) => {
        if (e.lengthComputable && e.total > 0) onProgress?.(e.loaded / e.total)
      }
      const interrupted = () => reject(new ApiError(0, { title: 'The upload was refused or interrupted: check the file size limit and try again.' }))
      xhr.onerror = interrupted
      xhr.onabort = interrupted
      xhr.ontimeout = interrupted
      xhr.onload = () => {
        if (xhr.status === 401 && token) window.dispatchEvent(new Event(UNAUTHORIZED_EVENT))
        if (xhr.status === 200 || xhr.status === 202) {
          try {
            resolve({ submission: JSON.parse(xhr.responseText) as SubmissionDto, alreadyUploaded: xhr.status === 200 })
          } catch {
            reject(new ApiError(xhr.status, { title: 'The server sent an answer Verso could not read.' }))
          }
          return
        }
        let problem: Problem = { title: `Request failed (HTTP ${xhr.status}).` }
        try {
          if (xhr.responseText) problem = JSON.parse(xhr.responseText) as Problem
        } catch {
          // keep the generic problem
        }
        reject(new ApiError(xhr.status, problem))
      }
      xhr.send(form)
    })
  },
}
